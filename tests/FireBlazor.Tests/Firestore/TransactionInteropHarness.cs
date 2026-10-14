using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;

namespace FireBlazor.Tests.Firestore;

/// <summary>
/// A <see cref="JSRuntime"/> that plays the JS side of <c>fireblazor.js</c>'s Firestore transaction exports
/// (<c>firestoreRunTransactionWithCallback</c>, <c>firestoreBatchWrite</c>) against an in-memory document store.
/// Calls back into .NET go through the real <see cref="DotNetDispatcher"/>, so its argument binding and
/// result serialization (the step that threw on a failed <c>Result&lt;T&gt;</c> and left the JS promise
/// unsettled) run exactly as they do in the browser. A .NET call whose result never comes back surfaces as a
/// hang, which <see cref="AwaitOrHangAsync{T}"/> turns into a test failure.
/// </summary>
internal sealed class TransactionInteropHarness : JSRuntime
{
    private const long ModuleId = 1;
    private long _nextObjectId = ModuleId;
    private long _nextCallId;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _dotNetCalls = new();
    private readonly ConcurrentDictionary<long, string> _transactionReaders = new();

    /// <summary>Committed documents by path. Values are the stored data as JSON.</summary>
    public ConcurrentDictionary<string, JsonObject> Documents { get; } = new();

    /// <summary>Every path the JS side read inside a transaction, in order.</summary>
    public ConcurrentQueue<string> TransactionReads { get; } = new();

    public int TransactionAttempts;

    /// <summary>
    /// True (current fireblazor.js): call <c>ProcessTransactionWithReader</c> with a reader for paths the dry
    /// run missed. False: behave like a cached pre-reader fireblazor.js calling <c>ProcessTransaction</c>.
    /// </summary>
    public bool PassTransactionReader { get; init; } = true;

    public static async Task<T> AwaitOrHangAsync<T>(Task<T> task, int timeoutMs = 5000)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(
            ReferenceEquals(finished, task),
            $"The transaction did not complete within {timeoutMs} ms (the interop call hung).");
        return await task;
    }

    protected override void BeginInvokeJS(
        long taskId, string identifier, string? argsJson, JSCallResultType resultType, long targetInstanceId)
    {
        _ = Task.Run(async () =>
        {
            string resultJson;
            try
            {
                var args = JsonNode.Parse(argsJson ?? "[]")!.AsArray();
                resultJson = targetInstanceId switch
                {
                    0 when identifier == "import" => $$"""{"__jsObjectId":{{ModuleId}}}""",
                    ModuleId => await InvokeModuleAsync(identifier, args),
                    _ when _transactionReaders.TryGetValue(targetInstanceId, out _) && identifier == "get"
                        => ReadInTransaction(args[0]!.GetValue<string>()).ToJsonString(),
                    _ => throw new InvalidOperationException($"Unexpected JS call {identifier} on #{targetInstanceId}"),
                };
            }
            catch (Exception ex)
            {
                DotNetDispatcher.EndInvokeJS(this, JsonSerializer.Serialize(new object[] { taskId, false, ex.Message }));
                return;
            }

            DotNetDispatcher.EndInvokeJS(this, $"[{taskId},true,{resultJson}]");
        });
    }

    protected override void EndInvokeDotNet(DotNetInvocationInfo invocationInfo, in DotNetInvocationResult invocationResult)
    {
        if (invocationInfo.CallId is not { } callId || !_dotNetCalls.TryRemove(callId, out var pending))
            return;

        if (invocationResult.Success)
            pending.SetResult(invocationResult.ResultJson ?? "null");
        else
            pending.SetException(new InvalidOperationException(invocationResult.Exception?.Message));
    }

    private Task<string> InvokeDotNetAsync(long dotNetObjectId, string method, params JsonNode?[] args)
    {
        var callId = Interlocked.Increment(ref _nextCallId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dotNetCalls[callId] = pending;
        var argsJson = new JsonArray(args.Select(a => a?.DeepClone()).ToArray()).ToJsonString();
        DotNetDispatcher.BeginInvokeDotNet(this, new DotNetInvocationInfo(null, method, dotNetObjectId, callId), argsJson);
        return pending.Task;
    }

    private async Task<string> InvokeModuleAsync(string identifier, JsonArray args) => identifier switch
    {
        "firestoreRunTransactionWithCallback" => await RunTransactionWithCallbackAsync(args),
        "firestoreBatchWrite" => ApplyWrites(args[0]!.AsArray()),
        _ => throw new InvalidOperationException($"Unexpected module export {identifier}"),
    };

    private JsonObject ReadInTransaction(string path)
    {
        TransactionReads.Enqueue(path);
        var exists = Documents.TryGetValue(path, out var data);
        return new JsonObject
        {
            ["path"] = path,
            ["id"] = path[(path.LastIndexOf('/') + 1)..],
            ["exists"] = exists,
            ["data"] = exists ? data!.DeepClone() : null,
        };
    }

    // Mirrors fireblazor.js firestoreRunTransactionWithCallback: pre-read the dry run's paths, hand them to
    // .NET with a reader for anything the dry run missed, then apply the returned writes.
    private async Task<string> RunTransactionWithCallbackAsync(JsonArray args)
    {
        Interlocked.Increment(ref TransactionAttempts);
        var readPaths = args[0]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        var dotNetObjectId = args[1]!["__dotNetObject"]!.GetValue<long>();

        var readResults = new JsonObject();
        foreach (var path in readPaths)
            readResults[path] = ReadInTransaction(path);

        var readerId = Interlocked.Increment(ref _nextObjectId);
        _transactionReaders[readerId] = "reader";
        string callbackJson;
        try
        {
            callbackJson = PassTransactionReader
                ? await InvokeDotNetAsync(
                    dotNetObjectId, "ProcessTransactionWithReader", readResults, new JsonObject { ["__jsObjectId"] = readerId })
                : await InvokeDotNetAsync(dotNetObjectId, "ProcessTransaction", readResults);
        }
        catch (Exception ex)
        {
            return Failure("firestore/unknown", ex.Message);
        }
        finally
        {
            _transactionReaders.TryRemove(readerId, out _);
        }

        var callback = JsonNode.Parse(callbackJson)!.AsObject();
        if (callback["error"] is { } error && error.GetValueKind() == JsonValueKind.String)
            return Failure("firestore/unknown", error.GetValue<string>());

        ApplyWrites(callback["operations"]!.AsArray());
        return """{"success":true,"data":null}""";
    }

    private string ApplyWrites(JsonArray operations)
    {
        foreach (var op in operations)
        {
            var path = op!["path"]!.GetValue<string>();
            switch (op["type"]!.GetValue<string>())
            {
                case "set":
                    Documents[path] = op["data"]!.DeepClone().AsObject();
                    break;
                case "update":
                    var target = Documents.GetOrAdd(path, _ => new JsonObject());
                    foreach (var (key, value) in op["data"]!.AsObject())
                        target[key] = value?.DeepClone();
                    break;
                case "delete":
                    Documents.TryRemove(path, out _);
                    break;
            }
        }

        return """{"success":true,"data":null}""";
    }

    private static string Failure(string code, string message) =>
        new JsonObject
        {
            ["success"] = false,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();
}
