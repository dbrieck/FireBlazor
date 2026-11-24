using System.Reflection;
using System.Text.Json;
using FireBlazor.Platform.Wasm;
using Microsoft.JSInterop;
using NSubstitute;

namespace FireBlazor.Tests.Firestore;

/// <summary>
/// <see cref="ICollectionReference{T}.OnSnapshotWithMetadata"/>: a collection listener that also reports the query
/// snapshot's metadata, so a caller can tell an empty result served from the offline cache from a server-confirmed one.
/// Additive: <see cref="ICollectionReference{T}.OnSnapshot"/> and its JS call are unchanged, and an implementer that predates
/// the member gets a default that reports <see langword="null"/> (unknown) metadata.
/// </summary>
public class QuerySnapshotMetadataTests
{
    public class Link
    {
        public string? WorkspaceId { get; set; }
    }

    private static readonly JsonElement[] OneDoc =
    [
        JsonDocument.Parse("""{"id":"WS1","path":"links/u1/items/WS1","exists":true,"data":{"workspaceId":"WS1"}}""").RootElement,
    ];

    [Theory]
    [InlineData("""{"isFromCache":true,"hasPendingWrites":false}""", true, false)]
    [InlineData("""{"isFromCache":false,"hasPendingWrites":true}""", false, true)]
    [InlineData("""{"isFromCache":false}""", false, false)]
    public void ParseQueryMetadata_reads_fromCache_and_pending_writes(string json, bool fromCache, bool pending)
    {
        var meta = SnapshotParser.ParseQueryMetadata(JsonDocument.Parse(json).RootElement);

        Assert.NotNull(meta);
        Assert.Equal(fromCache, meta!.IsFromCache);
        Assert.Equal(pending, meta.HasPendingWrites);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"isFromCache":"yes"}""")]
    [InlineData("[]")]
    public void ParseQueryMetadata_is_unknown_not_server_confirmed_when_missing_or_malformed(string json)
    {
        Assert.Null(SnapshotParser.ParseQueryMetadata(JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public async Task OnSnapshotWithMetadata_subscribes_with_the_metadata_flag_and_delivers_docs_with_their_metadata()
    {
        var (jsInterop, module) = Interop();
        var firestore = new WasmFirestore(jsInterop);
        var delivered = new TaskCompletionSource<(IReadOnlyList<DocumentSnapshot<Link>> Docs, SnapshotMetadata? Meta)>();

        var unsubscribe = firestore.Collection<Link>("links/u1/items")
            .OnSnapshotWithMetadata((docs, meta) => delivered.TrySetResult((docs, meta)));

        var call = await WaitForSubscribeCallAsync(module);
        Assert.Equal(4, call.Length);
        Assert.Equal("links/u1/items", call[0]);
        Assert.Equal(true, call[3]);

        var callback = (DotNetObjectReference<ISnapshotCallback>)call[2]!;
        callback.Value.OnCollectionSnapshotWithMetadata(
            [],
            JsonDocument.Parse("""{"isFromCache":true,"hasPendingWrites":false}""").RootElement);
        var cached = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(cached.Docs);
        Assert.True(cached.Meta!.IsFromCache);

        delivered = new TaskCompletionSource<(IReadOnlyList<DocumentSnapshot<Link>> Docs, SnapshotMetadata? Meta)>();
        callback.Value.OnCollectionSnapshotWithMetadata(
            OneDoc,
            JsonDocument.Parse("""{"isFromCache":false,"hasPendingWrites":false}""").RootElement);
        var server = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("WS1", Assert.Single(server.Docs).Data!.WorkspaceId);
        Assert.False(server.Meta!.IsFromCache);

        await unsubscribe();
    }

    [Fact]
    public async Task OnSnapshot_keeps_its_original_JS_call_without_the_metadata_flag()
    {
        var (jsInterop, module) = Interop();
        var firestore = new WasmFirestore(jsInterop);
        var delivered = new TaskCompletionSource<IReadOnlyList<DocumentSnapshot<Link>>>();

        var unsubscribe = firestore.Collection<Link>("links/u1/items").OnSnapshot(docs => delivered.TrySetResult(docs));

        var call = await WaitForSubscribeCallAsync(module);
        Assert.Equal(3, call.Length);

        ((DotNetObjectReference<ISnapshotCallback>)call[2]!).Value.OnCollectionSnapshot(OneDoc);
        Assert.Single(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await unsubscribe();
    }

    [Fact]
    public void The_metadata_callback_is_JS_invokable_on_the_subscription_type_by_its_JS_name()
    {
        // fireblazor.js invokes 'OnCollectionSnapshotWithMetadata' with (docs, metadata) on the DotNetObjectReference.
        var method = typeof(CollectionSnapshotSubscription<Link>).GetMethod(
            "OnCollectionSnapshotWithMetadata", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<JSInvokableAttribute>());
        Assert.Equal([typeof(JsonElement[]), typeof(JsonElement)], method.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void An_implementer_that_predates_the_member_reports_unknown_metadata()
    {
        var legacy = new LegacyCollection();
        IReadOnlyList<DocumentSnapshot<Link>>? docs = null;
        var meta = new SnapshotMetadata { IsFromCache = true };

        ((ICollectionReference<Link>)legacy).OnSnapshotWithMetadata((d, m) =>
        {
            docs = d;
            meta = m;
        });
        legacy.Emit([]);

        Assert.NotNull(docs);
        Assert.Null(meta);
    }

    private static (FirebaseJsInterop Interop, IJSObjectReference Module) Interop()
    {
        var jsRuntime = Substitute.For<IJSRuntime>();
        var module = Substitute.For<IJSObjectReference>();
        jsRuntime.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>())
            .Returns(new ValueTask<IJSObjectReference>(module));
        module.InvokeAsync<JsResult<JsSubscriptionResult>>("firestoreSubscribeCollection", Arg.Any<object?[]?>())
            .Returns(new ValueTask<JsResult<JsSubscriptionResult>>(
                new JsResult<JsSubscriptionResult> { Success = true, Data = new JsSubscriptionResult { SubscriptionId = 7 } }));
        return (new FirebaseJsInterop(jsRuntime), module);
    }

    private static async Task<object?[]> WaitForSubscribeCallAsync(IJSObjectReference module)
    {
        for (var i = 0; i < 100; i++)
        {
            var call = module.ReceivedCalls()
                .FirstOrDefault(c => c.GetMethodInfo().Name == nameof(IJSObjectReference.InvokeAsync)
                    && Equals(c.GetArguments()[0], "firestoreSubscribeCollection"));
            if (call is not null)
            {
                return (object?[])call.GetArguments()[1]!;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException("firestoreSubscribeCollection was never called");
    }

    /// <summary>An implementer written before <see cref="ICollectionReference{T}.OnSnapshotWithMetadata"/> existed.</summary>
    private sealed class LegacyCollection : ICollectionReference<Link>
    {
        private Action<IReadOnlyList<DocumentSnapshot<Link>>>? _onNext;

        public void Emit(IReadOnlyList<DocumentSnapshot<Link>> docs) => _onNext?.Invoke(docs);

        public Func<ValueTask> OnSnapshot(Action<IReadOnlyList<DocumentSnapshot<Link>>> onNext, Action<Exception>? onError = null)
        {
            _onNext = onNext;
            return () => ValueTask.CompletedTask;
        }

        public ICollectionReference<Link> Where(System.Linq.Expressions.Expression<Func<Link, bool>> predicate) => throw new NotSupportedException();
        public ICollectionReference<Link> OrderBy<TKey>(System.Linq.Expressions.Expression<Func<Link, TKey>> keySelector) => throw new NotSupportedException();
        public ICollectionReference<Link> OrderByDescending<TKey>(System.Linq.Expressions.Expression<Func<Link, TKey>> keySelector) => throw new NotSupportedException();
        public ICollectionReference<Link> OrderByDocumentId() => throw new NotSupportedException();
        public ICollectionReference<Link> Take(int count) => throw new NotSupportedException();
        public ICollectionReference<Link> Skip(int count) => throw new NotSupportedException();
        public IDocumentReference<Link> Document(string id) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<DocumentSnapshot<Link>>>> GetAsync() => throw new NotSupportedException();
        public Task<Result<DocumentReference>> AddAsync(Link data) => throw new NotSupportedException();
        public IAggregateQuery Aggregate() => throw new NotSupportedException();
        public ICollectionReference<Link> StartAt(params object[] fieldValues) => throw new NotSupportedException();
        public ICollectionReference<Link> StartAfter(params object[] fieldValues) => throw new NotSupportedException();
        public ICollectionReference<Link> EndAt(params object[] fieldValues) => throw new NotSupportedException();
        public ICollectionReference<Link> EndBefore(params object[] fieldValues) => throw new NotSupportedException();
    }
}
