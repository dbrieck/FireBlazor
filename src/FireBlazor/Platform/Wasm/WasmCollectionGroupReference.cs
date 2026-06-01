using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.JSInterop;

namespace FireBlazor.Platform.Wasm;

/// <summary>
/// WebAssembly implementation of ICollectionGroupReference using JavaScript interop.
/// </summary>
internal sealed class WasmCollectionGroupReference<T> : ICollectionGroupReference<T> where T : class
{
    private readonly FirebaseJsInterop _jsInterop;
    private readonly string _collectionId;
    private readonly List<WhereClause> _whereClauses = [];
    private readonly List<OrderByClause> _orderByClauses = [];
    private int? _limit;
    private object[]? _startAt;
    private object[]? _startAfter;

    public WasmCollectionGroupReference(FirebaseJsInterop jsInterop, string collectionId)
    {
        _jsInterop = jsInterop ?? throw new ArgumentNullException(nameof(jsInterop));
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        _collectionId = collectionId;
    }

    private WasmCollectionGroupReference(
        FirebaseJsInterop jsInterop,
        string collectionId,
        List<WhereClause> whereClauses,
        List<OrderByClause> orderByClauses,
        int? limit,
        object[]? startAt = null,
        object[]? startAfter = null)
    {
        _jsInterop = jsInterop;
        _collectionId = collectionId;
        _whereClauses = [.. whereClauses];
        _orderByClauses = [.. orderByClauses];
        _limit = limit;
        _startAt = startAt;
        _startAfter = startAfter;
    }

    public ICollectionGroupReference<T> Where(Expression<Func<T, bool>> predicate)
    {
        var visitor = new WhereExpressionVisitor();
        visitor.Visit(predicate);

        var newClauses = new List<WhereClause>(_whereClauses);
        newClauses.AddRange(visitor.Clauses);

        return new WasmCollectionGroupReference<T>(_jsInterop, _collectionId, newClauses, _orderByClauses, _limit,
            _startAt, _startAfter);
    }

    public ICollectionGroupReference<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var memberName = GetMemberName(keySelector);
        var newOrderBy = new List<OrderByClause>(_orderByClauses)
        {
            new(memberName, "asc")
        };

        return new WasmCollectionGroupReference<T>(_jsInterop, _collectionId, _whereClauses, newOrderBy, _limit,
            _startAt, _startAfter);
    }

    public ICollectionGroupReference<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var memberName = GetMemberName(keySelector);
        var newOrderBy = new List<OrderByClause>(_orderByClauses)
        {
            new(memberName, "desc")
        };

        return new WasmCollectionGroupReference<T>(_jsInterop, _collectionId, _whereClauses, newOrderBy, _limit,
            _startAt, _startAfter);
    }

    public ICollectionGroupReference<T> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return new WasmCollectionGroupReference<T>(_jsInterop, _collectionId, _whereClauses, _orderByClauses, count,
            _startAt, _startAfter);
    }

    public async Task<Result<IReadOnlyList<DocumentSnapshot<T>>>> GetAsync()
    {
        var hasCursors = _startAt != null || _startAfter != null;
        if (hasCursors && _orderByClauses.Count == 0)
        {
            throw new InvalidOperationException(
                "Cursor methods (StartAt, StartAfter) require OrderBy to be specified first. " +
                "Firestore cursors are based on field values in the order specified by OrderBy clauses.");
        }

        var queryParams = BuildQueryParams();
        var result = await _jsInterop.FirestoreCollectionGroupQueryAsync(_collectionId, queryParams!);

        if (!result.Success)
            return Result<IReadOnlyList<DocumentSnapshot<T>>>.Failure(
                new FirebaseError(result.Error!.Code, result.Error.Message));

        var snapshots = SnapshotParser.ParseMany<T>(result.Data);
        return Result<IReadOnlyList<DocumentSnapshot<T>>>.Success(snapshots);
    }

    public Func<ValueTask> OnSnapshot(Action<IReadOnlyList<DocumentSnapshot<T>>> onNext, Action<Exception>? onError = null)
    {
        var queryParams = BuildQueryParams();
        var subscription = new CollectionGroupSnapshotSubscription<T>(
            _jsInterop, _collectionId, queryParams, onNext, onError);
        subscription.StartAsync().ConfigureAwait(false);
        return () => subscription.DisposeAsync();
    }

    public ICollectionGroupReference<T> StartAt(params object[] fieldValues)
    {
        ArgumentNullException.ThrowIfNull(fieldValues);
        if (fieldValues.Length == 0)
            throw new ArgumentException("At least one field value is required", nameof(fieldValues));

        return new WasmCollectionGroupReference<T>(_jsInterop, _collectionId, _whereClauses, _orderByClauses, _limit,
            fieldValues, _startAfter);
    }

    public ICollectionGroupReference<T> StartAfter(params object[] fieldValues)
    {
        ArgumentNullException.ThrowIfNull(fieldValues);
        if (fieldValues.Length == 0)
            throw new ArgumentException("At least one field value is required", nameof(fieldValues));

        return new WasmCollectionGroupReference<T>(_jsInterop, _collectionId, _whereClauses, _orderByClauses, _limit,
            _startAt, fieldValues);
    }

    private object BuildQueryParams()
    {
        return new
        {
            where = _whereClauses.Count > 0
                ? _whereClauses.Select(w => new { field = w.Field, op = w.Operator, value = w.Value }).ToArray()
                : null,
            orderBy = _orderByClauses.Count > 0
                ? _orderByClauses.Select(o => new { field = o.Field, direction = o.Direction }).ToArray()
                : null,
            limit = _limit,
            startAt = _startAt,
            startAfter = _startAfter
        };
    }

    private static string GetMemberName<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        if (keySelector.Body is MemberExpression memberExpr)
            return CamelCaseHelper.ToCamelCase(memberExpr.Member.Name);

        throw new ArgumentException("Expression must be a member access expression", nameof(keySelector));
    }
}

/// <summary>
/// Manages a real-time subscription to a Firestore collection-group query.
/// </summary>
internal sealed class CollectionGroupSnapshotSubscription<T> : ISnapshotCallback, IDisposable, IAsyncDisposable where T : class
{
    private readonly FirebaseJsInterop _jsInterop;
    private readonly string _collectionId;
    private readonly object? _queryParams;
    private readonly Action<IReadOnlyList<DocumentSnapshot<T>>> _onNext;
    private readonly Action<Exception>? _onError;
    private readonly object _lock = new();
    private DotNetObjectReference<ISnapshotCallback>? _callbackRef;
    private int _subscriptionId;
    private bool _disposed;

    public CollectionGroupSnapshotSubscription(
        FirebaseJsInterop jsInterop,
        string collectionId,
        object? queryParams,
        Action<IReadOnlyList<DocumentSnapshot<T>>> onNext,
        Action<Exception>? onError)
    {
        _jsInterop = jsInterop;
        _collectionId = collectionId;
        _queryParams = queryParams;
        _onNext = onNext;
        _onError = onError;
    }

    public async Task StartAsync()
    {
        DotNetObjectReference<ISnapshotCallback>? callbackRef;

        lock (_lock)
        {
            if (_disposed) return;
            callbackRef = DotNetObjectReference.Create<ISnapshotCallback>(this);
            _callbackRef = callbackRef;
        }

        try
        {
            var result = await _jsInterop.FirestoreSubscribeCollectionGroupAsync(_collectionId, _queryParams, callbackRef);

            lock (_lock)
            {
                if (_disposed)
                {
                    if (result.Success && result.Data != null)
                    {
                        _ = UnsubscribeAsync(result.Data.SubscriptionId);
                    }
                    callbackRef.Dispose();
                    _callbackRef = null;
                    return;
                }

                if (result.Success && result.Data != null)
                {
                    _subscriptionId = result.Data.SubscriptionId;
                }
                else if (result.Error != null)
                {
                    callbackRef.Dispose();
                    _callbackRef = null;
                    _onError?.Invoke(new FirebaseException(result.Error.Code, result.Error.Message));
                }
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                callbackRef.Dispose();
                _callbackRef = null;
            }
            _onError?.Invoke(ex);
        }
    }

    [JSInvokable]
    public void OnDocumentSnapshot(JsonElement data)
    {
    }

    [JSInvokable]
    public void OnCollectionSnapshot(JsonElement[] data) =>
        _ = DispatchSnapshotAsync(() =>
        {
            var snapshots = new List<DocumentSnapshot<T>>(data.Length);
            foreach (var item in data)
            {
                snapshots.Add(SnapshotParser.Parse<T>(item));
            }

            _onNext(snapshots);
        });

    [JSInvokable]
    public void OnSnapshotError(JsError error) =>
        _ = DispatchSnapshotAsync(() =>
            _onError?.Invoke(new FirebaseException(error.Code, error.Message)));

    private Task DispatchSnapshotAsync(Action dispatch)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }
        }

        return DispatchSnapshotCoreAsync(dispatch);
    }

    private async Task DispatchSnapshotCoreAsync(Action dispatch)
    {
        await Task.Yield();

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            dispatch();
        }
        catch (Exception ex)
        {
            try
            {
                _onError?.Invoke(ex);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (!TryBeginDispose(out var subscriptionId, out var callbackRef))
        {
            return;
        }

        _ = CompleteDisposeAsync(subscriptionId, callbackRef);
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDispose(out var subscriptionId, out var callbackRef))
        {
            return;
        }

        await CompleteDisposeAsync(subscriptionId, callbackRef);
    }

    private bool TryBeginDispose(out int subscriptionId, out DotNetObjectReference<ISnapshotCallback>? callbackRef)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                subscriptionId = 0;
                callbackRef = null;
                return false;
            }

            _disposed = true;
            subscriptionId = _subscriptionId;
            callbackRef = _callbackRef;
            _callbackRef = null;
            return true;
        }
    }

    private async Task CompleteDisposeAsync(int subscriptionId, DotNetObjectReference<ISnapshotCallback>? callbackRef)
    {
        if (subscriptionId > 0)
        {
            await UnsubscribeAsync(subscriptionId);
        }

        callbackRef?.Dispose();
    }

    private async Task UnsubscribeAsync(int subscriptionId)
    {
        try
        {
            await _jsInterop.FirestoreUnsubscribeAsync(subscriptionId);
        }
        catch
        {
        }
    }
}
