using System.Text.Json;
using Microsoft.JSInterop;

namespace FireBlazor.Platform.Wasm;

/// <summary>
/// WebAssembly implementation of IFirestore using JavaScript interop.
/// </summary>
internal sealed class WasmFirestore : IFirestore
{
    private readonly FirebaseJsInterop _jsInterop;

    public WasmFirestore(FirebaseJsInterop jsInterop)
    {
        _jsInterop = jsInterop ?? throw new ArgumentNullException(nameof(jsInterop));
    }

    public ICollectionReference<T> Collection<T>(string path) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new WasmCollectionReference<T>(_jsInterop, path);
    }

    public ICollectionGroupReference<T> CollectionGroup<T>(string collectionId) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        if (collectionId.Contains('/'))
        {
            throw new ArgumentException(
                "Collection group id must be a single collection segment, not a path.",
                nameof(collectionId));
        }

        return new WasmCollectionGroupReference<T>(_jsInterop, collectionId);
    }

    public async Task<Result<Unit>> ClearPersistenceAsync()
    {
        var result = await _jsInterop.ClearFirestorePersistenceAsync();
        if (!result.Success)
            return Result<Unit>.Failure(new FirebaseError(
                result.Error?.Code ?? "firestore/clear-persistence-failed",
                result.Error?.Message ?? "Failed to clear Firestore persistence."));

        return Unit.Value;
    }

    public async Task<Result<Unit>> BatchAsync(Action<IWriteBatch> operations)
    {
        var batch = new WasmWriteBatch(_jsInterop);
        operations(batch);
        return await batch.CommitAsync();
    }

    public async Task<Result<T>> TransactionAsync<T>(Func<ITransaction, Task<T>> operations)
    {
        // Phase 1: Collect document paths to read (dry run with collecting transaction)
        var collectingTx = new CollectingTransaction();
        try
        {
            await operations(collectingTx);
        }
        catch
        {
            // Ignore exceptions in dry run - we just need the read paths
        }

        var readPaths = collectingTx.ReadPaths;

        // The dry run sees Exists=false placeholders, so a body that bails out on "not found" stops before
        // its later reads and the collected paths are a lower bound, not the read set. Phase 2 reads any
        // path it missed inside the live transaction; this phase only skips the transaction when the real
        // run turns out to need no reads at all.
        if (readPaths.Count == 0)
        {
            try
            {
                var directTx = new DirectTransaction();
                T result;
                try
                {
                    result = await operations(directTx);
                }
                catch (DryRunMissedReadException)
                {
                    // The real run reads after all: discard its buffered writes and run it as a transaction.
                    return await RunWithCallbackAsync(readPaths, operations);
                }

                // Same, for a body that caught the exception itself instead of letting it propagate.
                if (directTx.AttemptedRead)
                    return await RunWithCallbackAsync(readPaths, operations);

                if (directTx.Operations.Count > 0)
                {
                    var batchResult = await _jsInterop.FirestoreBatchWriteAsync(
                        directTx.Operations.Select(op => new BatchOperation
                        {
                            Type = op.Type,
                            Path = op.Path,
                            Data = op.Data,
                            Merge = op.Merge
                        }));

                    if (!batchResult.Success)
                    {
                        var err = new FirebaseError(batchResult.Error!.Code, batchResult.Error.Message);
                        FirestoreWriteFailureSink.Notify("transaction", "(transaction)", err);
                        return Result<T>.Failure(err);
                    }
                }

                return Result<T>.Success(result);
            }
            catch (Exception ex)
            {
                var err = new FirebaseError("firestore/unknown", ex.Message);
                FirestoreWriteFailureSink.Notify("transaction", "(transaction)", err);
                return Result<T>.Failure(err);
            }
        }

        return await RunWithCallbackAsync(readPaths, operations);
    }

    private async Task<Result<T>> RunWithCallbackAsync<T>(
        IReadOnlyList<string> readPaths, Func<ITransaction, Task<T>> operations)
    {
        // Phase 2: Execute transaction with callback
        var handler = new TransactionHandler<T>(_jsInterop, operations);
        var callbackRef = DotNetObjectReference.Create(handler);

        try
        {
            var result = await _jsInterop.FirestoreRunTransactionWithCallbackAsync(readPaths, callbackRef);

            if (!result.Success)
            {
                var err = new FirebaseError(result.Error!.Code, result.Error.Message);
                FirestoreWriteFailureSink.Notify("transaction", "(transaction)", err);
                return Result<T>.Failure(err);
            }

            if (handler.Exception != null)
            {
                var err = new FirebaseError("firestore/unknown", handler.Exception.Message);
                FirestoreWriteFailureSink.Notify("transaction", "(transaction)", err);
                return Result<T>.Failure(err);
            }

            return Result<T>.Success(handler.Result!);
        }
        catch (Exception ex)
        {
            var err = new FirebaseError("firestore/unknown", ex.Message);
            FirestoreWriteFailureSink.Notify("transaction", "(transaction)", err);
            return Result<T>.Failure(err);
        }
        finally
        {
            callbackRef.Dispose();
        }
    }
}

/// <summary>
/// Transaction that only collects document paths for reads (used in dry run).
/// </summary>
internal sealed class CollectingTransaction : ITransaction
{
    private readonly List<string> _readPaths = [];

    public IReadOnlyList<string> ReadPaths => _readPaths;

    public Task<Result<DocumentSnapshot<T>>> GetAsync<T>(IDocumentReference<T> doc) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        _readPaths.Add(doc.Path);

        // Return placeholder - this data won't be used
        return Task.FromResult(Result<DocumentSnapshot<T>>.Success(
            new DocumentSnapshot<T>
            {
                Id = doc.Id,
                Path = doc.Path,
                Data = default,
                Exists = false,
                Metadata = new SnapshotMetadata { IsFromCache = false, HasPendingWrites = false }
            }));
    }

    public ITransaction Set<T>(IDocumentReference<T> doc, T data) where T : class => this;
    public ITransaction Update<T>(IDocumentReference<T> doc, object fields) where T : class => this;
    public ITransaction Delete<T>(IDocumentReference<T> doc) where T : class => this;
}

/// <summary>
/// Thrown by <see cref="DirectTransaction.GetAsync{T}"/> when a body the dry run saw make no reads reads after
/// all; <see cref="WasmFirestore"/> catches it and re-runs the body as a real transaction.
/// </summary>
internal sealed class DryRunMissedReadException : InvalidOperationException
{
    public DryRunMissedReadException()
        : base("The transaction read a document its dry run did not; re-running it as a transaction.")
    {
    }
}

/// <summary>
/// Transaction for direct execution without reads (uses batch write).
/// </summary>
internal sealed class DirectTransaction : ITransaction
{
    private readonly List<TransactionOperation> _operations = [];

    public IReadOnlyList<TransactionOperation> Operations => _operations;

    /// <summary>True once the body tried to read: the batch-write shortcut no longer applies.</summary>
    public bool AttemptedRead { get; private set; }

    public Task<Result<DocumentSnapshot<T>>> GetAsync<T>(IDocumentReference<T> doc) where T : class
    {
        AttemptedRead = true;
        throw new DryRunMissedReadException();
    }

    public ITransaction Set<T>(IDocumentReference<T> doc, T data) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(data);
        _operations.Add(new TransactionOperation { Type = "set", Path = doc.Path, Data = data, Merge = false });
        return this;
    }

    public ITransaction Update<T>(IDocumentReference<T> doc, object fields) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(fields);
        _operations.Add(new TransactionOperation { Type = "update", Path = doc.Path, Data = fields });
        return this;
    }

    public ITransaction Delete<T>(IDocumentReference<T> doc) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        _operations.Add(new TransactionOperation { Type = "delete", Path = doc.Path });
        return this;
    }
}

/// <summary>
/// Handles the callback from JS during transaction execution.
/// </summary>
internal sealed class TransactionHandler<T>
{
    private readonly FirebaseJsInterop _jsInterop;
    private readonly Func<ITransaction, Task<T>> _operations;

    public T? Result { get; private set; }
    public Exception? Exception { get; private set; }

    public TransactionHandler(FirebaseJsInterop jsInterop, Func<ITransaction, Task<T>> operations)
    {
        _jsInterop = jsInterop;
        _operations = operations;
    }

    /// <summary>
    /// Entry point of a <c>fireblazor.js</c> that predates the transaction reader (the module URL is not
    /// fingerprinted, so a browser can pair a cached copy with a newer assembly).
    /// </summary>
    [JSInvokable]
    public Task<TransactionCallbackResult> ProcessTransaction(Dictionary<string, JsonElement> readData)
        => ProcessTransactionWithReader(readData, null);

    /// <param name="readData">The documents JS pre-read for the paths the dry run collected.</param>
    /// <param name="reader">
    /// JS object whose <c>get(path)</c> reads a document inside the same live transaction, for paths the dry
    /// run never reached (its Exists=false placeholders make bodies bail out early). Null on hosts without
    /// <c>DotNet.createJSObjectReference</c>; such reads then fail as "not pre-read".
    /// </param>
    [JSInvokable]
    public async Task<TransactionCallbackResult> ProcessTransactionWithReader(
        Dictionary<string, JsonElement> readData, IJSObjectReference? reader)
    {
        // Firestore re-invokes the callback on contention: each attempt starts clean.
        Result = default;
        Exception = null;
        try
        {
            var executingTx = new ExecutingTransaction(readData, reader);
            Result = await _operations(executingTx);

            // Serialize operations with FieldValueConverter
            var serializedOps = executingTx.Operations.Select(op => new
            {
                type = op.Type,
                path = op.Path,
                data = op.Data != null ? JsonSerializer.SerializeToElement(op.Data, FirestoreJsonOptions.Default) : (JsonElement?)null,
                merge = op.Merge
            }).ToList();

            // The body's result stays on this side (WasmFirestore reads it from Result). It is never sent to JS:
            // serializing it there runs after this try, inside Blazor's dispatcher, and a value that cannot
            // serialize (a failed Result<T>, whose Value getter throws) left the JS promise unsettled forever.
            return new TransactionCallbackResult
            {
                Operations = serializedOps.Cast<object>().ToList()
            };
        }
        catch (Exception ex)
        {
            Exception = ex;
            return new TransactionCallbackResult { Error = ex.Message };
        }
    }
}

/// <summary>
/// Result returned to JS from the transaction callback. Only JSON-safe members: anything here is serialized by
/// Blazor's dispatcher outside the callback's try/catch, so a throwing getter would hang the transaction.
/// </summary>
internal sealed class TransactionCallbackResult
{
    public List<object> Operations { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// Transaction that executes with real data from JS.
/// </summary>
internal sealed class ExecutingTransaction : ITransaction
{
    private readonly Dictionary<string, JsonElement> _readData;
    private readonly IJSObjectReference? _reader;
    private readonly List<TransactionOperation> _operations = [];

    public IReadOnlyList<TransactionOperation> Operations => _operations;

    public ExecutingTransaction(Dictionary<string, JsonElement> readData, IJSObjectReference? reader = null)
    {
        _readData = readData;
        _reader = reader;
    }

    public async Task<Result<DocumentSnapshot<T>>> GetAsync<T>(IDocumentReference<T> doc) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (!_readData.TryGetValue(doc.Path, out var data))
        {
            if (_reader is null)
            {
                return Result<DocumentSnapshot<T>>.Failure(
                    new FirebaseError("firestore/not-found", "Document was not pre-read in transaction"));
            }

            // A path the dry run never reached: read it inside the live transaction so it joins the read set.
            // Writes are buffered until the body returns, so this is still a read before any write.
            try
            {
                data = await _reader.InvokeAsync<JsonElement>("get", doc.Path);
            }
            catch (JSException ex)
            {
                return Result<DocumentSnapshot<T>>.Failure(new FirebaseError("firestore/unknown", ex.Message));
            }

            _readData[doc.Path] = data;
        }

        return Result<DocumentSnapshot<T>>.Success(ParseSnapshot<T>(data, doc.Id, doc.Path));
    }

    private static DocumentSnapshot<T> ParseSnapshot<T>(JsonElement item, string docId, string docPath) where T : class
    {
        var exists = item.TryGetProperty("exists", out var existsElement) && existsElement.GetBoolean();

        T? docData = default;
        if (exists && item.TryGetProperty("data", out var dataElement) && dataElement.ValueKind != JsonValueKind.Null)
        {
            docData = JsonSerializer.Deserialize<T>(dataElement.GetRawText(), FirestoreJsonOptions.Default);
        }

        return new DocumentSnapshot<T>
        {
            Id = docId,
            Path = docPath,
            Exists = exists,
            Data = docData,
            Metadata = new SnapshotMetadata { IsFromCache = false, HasPendingWrites = false }
        };
    }

    public ITransaction Set<T>(IDocumentReference<T> doc, T data) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(data);
        _operations.Add(new TransactionOperation { Type = "set", Path = doc.Path, Data = data, Merge = false });
        return this;
    }

    public ITransaction Update<T>(IDocumentReference<T> doc, object fields) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(fields);
        _operations.Add(new TransactionOperation { Type = "update", Path = doc.Path, Data = fields });
        return this;
    }

    public ITransaction Delete<T>(IDocumentReference<T> doc) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        _operations.Add(new TransactionOperation { Type = "delete", Path = doc.Path });
        return this;
    }
}

/// <summary>
/// WebAssembly implementation of IWriteBatch for atomic batch writes.
/// </summary>
internal sealed class WasmWriteBatch : IWriteBatch
{
    private readonly FirebaseJsInterop _jsInterop;
    private readonly List<BatchOperation> _operations = [];

    public WasmWriteBatch(FirebaseJsInterop jsInterop)
    {
        ArgumentNullException.ThrowIfNull(jsInterop);
        _jsInterop = jsInterop;
    }

    public IReadOnlyList<BatchOperation> Operations => _operations;

    public IWriteBatch Set<T>(IDocumentReference<T> doc, T data) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(data);
        _operations.Add(new BatchOperation
        {
            Type = "set",
            Path = doc.Path,
            Data = data,
            Merge = false
        });
        return this;
    }

    public IWriteBatch Update<T>(IDocumentReference<T> doc, object fields) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(fields);
        _operations.Add(new BatchOperation
        {
            Type = "update",
            Path = doc.Path,
            Data = fields
        });
        return this;
    }

    public IWriteBatch Delete<T>(IDocumentReference<T> doc) where T : class
    {
        ArgumentNullException.ThrowIfNull(doc);
        _operations.Add(new BatchOperation
        {
            Type = "delete",
            Path = doc.Path
        });
        return this;
    }

    internal async Task<Result<Unit>> CommitAsync()
    {
        if (_operations.Count == 0)
            return Unit.Value;

        if (_operations.Count > 500)
            return Result<Unit>.Failure(new FirebaseError(
                "firestore/invalid-argument",
                "Batch cannot contain more than 500 operations"));

        var result = await _jsInterop.FirestoreBatchWriteAsync(_operations);

        if (!result.Success)
        {
            var err = new FirebaseError(result.Error!.Code, result.Error.Message);
            var pathSummary = _operations.Count == 1
                ? _operations[0].Path
                : $"{_operations.Count} ops";
            FirestoreWriteFailureSink.Notify("batch", pathSummary ?? "(batch)", err);
            return Result<Unit>.Failure(err);
        }

        return Unit.Value;
    }
}
