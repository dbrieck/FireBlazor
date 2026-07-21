using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.JSInterop;

namespace FireBlazor.Platform.Wasm;

/// <summary>
/// WebAssembly implementation of ICollectionReference using JavaScript interop.
/// </summary>
internal sealed class WasmCollectionReference<T> : ICollectionReference<T> where T : class
{
    private readonly FirebaseJsInterop _jsInterop;
    private readonly string _path;
    private readonly List<WhereClause> _whereClauses = [];
    private readonly List<OrderByClause> _orderByClauses = [];
    private int? _limit;
    private object[]? _startAt;
    private object[]? _startAfter;
    private object[]? _endAt;
    private object[]? _endBefore;

    public WasmCollectionReference(FirebaseJsInterop jsInterop, string path)
    {
        _jsInterop = jsInterop ?? throw new ArgumentNullException(nameof(jsInterop));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    private WasmCollectionReference(
        FirebaseJsInterop jsInterop,
        string path,
        List<WhereClause> whereClauses,
        List<OrderByClause> orderByClauses,
        int? limit,
        object[]? startAt = null,
        object[]? startAfter = null,
        object[]? endAt = null,
        object[]? endBefore = null)
    {
        _jsInterop = jsInterop;
        _path = path;
        _whereClauses = [.. whereClauses];
        _orderByClauses = [.. orderByClauses];
        _limit = limit;
        _startAt = startAt;
        _startAfter = startAfter;
        _endAt = endAt;
        _endBefore = endBefore;
    }

    public ICollectionReference<T> Where(Expression<Func<T, bool>> predicate)
    {
        var visitor = new WhereExpressionVisitor();
        visitor.Visit(predicate);

        var newClauses = new List<WhereClause>(_whereClauses);
        newClauses.AddRange(visitor.Clauses);

        return new WasmCollectionReference<T>(_jsInterop, _path, newClauses, _orderByClauses, _limit,
            _startAt, _startAfter, _endAt, _endBefore);
    }

    public ICollectionReference<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var memberName = GetMemberName(keySelector);
        var newOrderBy = new List<OrderByClause>(_orderByClauses)
        {
            new(memberName, "asc")
        };

        return new WasmCollectionReference<T>(_jsInterop, _path, _whereClauses, newOrderBy, _limit,
            _startAt, _startAfter, _endAt, _endBefore);
    }

    public ICollectionReference<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var memberName = GetMemberName(keySelector);
        var newOrderBy = new List<OrderByClause>(_orderByClauses)
        {
            new(memberName, "desc")
        };

        return new WasmCollectionReference<T>(_jsInterop, _path, _whereClauses, newOrderBy, _limit,
            _startAt, _startAfter, _endAt, _endBefore);
    }

    public ICollectionReference<T> OrderByDocumentId()
    {
        const string DocumentIdField = "__documentId__";
        var newOrderBy = new List<OrderByClause>(_orderByClauses)
        {
            new(DocumentIdField, "asc")
        };

        return new WasmCollectionReference<T>(_jsInterop, _path, _whereClauses, newOrderBy, _limit,
            _startAt, _startAfter, _endAt, _endBefore);
    }

    public ICollectionReference<T> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return new WasmCollectionReference<T>(_jsInterop, _path, _whereClauses, _orderByClauses, count,
            _startAt, _startAfter, _endAt, _endBefore);
    }

    public ICollectionReference<T> Skip(int count)
    {
        // Firestore uses cursor-based pagination, not offset-based.
        // Skip() would require fetching and discarding documents, which is inefficient.
        // Use StartAfter/StartAt with document cursors instead (to be added in Phase 4).
        throw new NotSupportedException(
            "Skip() is not supported in Firestore. Use cursor-based pagination with StartAfter/StartAt instead.");
    }

    public IDocumentReference<T> Document(string id)
    {
        PathValidation.ValidatePath(id, nameof(id));
        return new WasmDocumentReference<T>(_jsInterop, $"{_path}/{id}");
    }

    public async Task<Result<IReadOnlyList<DocumentSnapshot<T>>>> GetAsync()
    {
        // Validate: cursors require orderBy (Firestore requirement)
        var hasCursors = _startAt != null || _startAfter != null || _endAt != null || _endBefore != null;
        if (hasCursors && _orderByClauses.Count == 0)
        {
            throw new InvalidOperationException(
                "Cursor methods (StartAt, StartAfter, EndAt, EndBefore) require OrderBy to be specified first. " +
                "Firestore cursors are based on field values in the order specified by OrderBy clauses.");
        }

        JsResult<JsonElement> result;
        var queryParams = BuildQueryParams();

        if (queryParams == null)
        {
            result = await _jsInterop.FirestoreGetAsync(_path);
        }
        else
        {
            result = await _jsInterop.FirestoreQueryAsync(_path, queryParams);
        }

        if (!result.Success)
            return Result<IReadOnlyList<DocumentSnapshot<T>>>.Failure(
                new FirebaseError(result.Error!.Code, result.Error.Message));

        var snapshots = SnapshotParser.ParseMany<T>(result.Data);
        return Result<IReadOnlyList<DocumentSnapshot<T>>>.Success(snapshots);
    }

    public async Task<Result<DocumentReference>> AddAsync(T data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var result = await _jsInterop.FirestoreAddAsync(_path, data);

        if (!result.Success)
            return Result<DocumentReference>.Failure(
                new FirebaseError(result.Error!.Code, result.Error.Message));

        return new DocumentReference
        {
            Id = result.Data!.Id,
            Path = result.Data.Path
        };
    }

    public Func<ValueTask> OnSnapshot(Action<IReadOnlyList<DocumentSnapshot<T>>> onNext, Action<Exception>? onError = null)
    {
        var queryParams = BuildQueryParams();
        var subscription = new CollectionSnapshotSubscription<T>(
            _jsInterop, _path, queryParams, onNext, onError);
        subscription.StartAsync().ConfigureAwait(false);
        return () => subscription.DisposeAsync();
    }

    public IAggregateQuery Aggregate()
    {
        var queryParams = BuildQueryParams();
        return new WasmAggregateQuery(_jsInterop, _path, queryParams);
    }

    public ICollectionReference<T> StartAt(params object[] fieldValues)
    {
        ArgumentNullException.ThrowIfNull(fieldValues);
        if (fieldValues.Length == 0)
            throw new ArgumentException("At least one field value is required", nameof(fieldValues));

        return new WasmCollectionReference<T>(_jsInterop, _path, _whereClauses, _orderByClauses, _limit,
            fieldValues, _startAfter, _endAt, _endBefore);
    }

    public ICollectionReference<T> StartAfter(params object[] fieldValues)
    {
        ArgumentNullException.ThrowIfNull(fieldValues);
        if (fieldValues.Length == 0)
            throw new ArgumentException("At least one field value is required", nameof(fieldValues));

        return new WasmCollectionReference<T>(_jsInterop, _path, _whereClauses, _orderByClauses, _limit,
            _startAt, fieldValues, _endAt, _endBefore);
    }

    public ICollectionReference<T> EndAt(params object[] fieldValues)
    {
        ArgumentNullException.ThrowIfNull(fieldValues);
        if (fieldValues.Length == 0)
            throw new ArgumentException("At least one field value is required", nameof(fieldValues));

        return new WasmCollectionReference<T>(_jsInterop, _path, _whereClauses, _orderByClauses, _limit,
            _startAt, _startAfter, fieldValues, _endBefore);
    }

    public ICollectionReference<T> EndBefore(params object[] fieldValues)
    {
        ArgumentNullException.ThrowIfNull(fieldValues);
        if (fieldValues.Length == 0)
            throw new ArgumentException("At least one field value is required", nameof(fieldValues));

        return new WasmCollectionReference<T>(_jsInterop, _path, _whereClauses, _orderByClauses, _limit,
            _startAt, _startAfter, _endAt, fieldValues);
    }

    private object? BuildQueryParams()
    {
        var hasParams = _whereClauses.Count > 0 || _orderByClauses.Count > 0 ||
                        _limit.HasValue || _startAt != null || _startAfter != null ||
                        _endAt != null || _endBefore != null;

        if (!hasParams)
            return null;

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
            startAfter = _startAfter,
            endAt = _endAt,
            endBefore = _endBefore
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
/// Represents a where clause in a Firestore query.
/// </summary>
internal sealed record WhereClause(string Field, string Operator, object? Value);

/// <summary>
/// Represents an order by clause in a Firestore query.
/// </summary>
internal sealed record OrderByClause(string Field, string Direction);

/// <summary>
/// Expression visitor to extract where clauses from LINQ expressions.
/// </summary>
internal sealed class WhereExpressionVisitor : ExpressionVisitor
{
    public List<WhereClause> Clauses { get; } = [];

    protected override Expression VisitBinary(BinaryExpression node)
    {
        if (node.NodeType == ExpressionType.AndAlso)
        {
            Visit(node.Left);
            Visit(node.Right);
            return node;
        }

        var (leftMember, leftValue, leftIsMember) = ExtractMemberAndValue(node.Left);
        var (rightMember, rightValue, rightIsMember) = ExtractMemberAndValue(node.Right);

        if (leftIsMember && !rightIsMember)
        {
            var field = CamelCaseHelper.ToCamelCase(leftMember!);
            var op = GetOperator(node.NodeType);
            Clauses.Add(new WhereClause(field, op, rightValue));
        }
        else if (!leftIsMember && rightIsMember)
        {
            var field = CamelCaseHelper.ToCamelCase(rightMember!);
            var op = GetReversedOperator(node.NodeType);
            Clauses.Add(new WhereClause(field, op, leftValue));
        }

        return node;
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Handle .Contains() for array-contains and in/not-in operators
        if (node.Method.Name == "Contains")
        {
            HandleContainsMethod(node, isNegated: false);
        }

        return base.VisitMethodCall(node);
    }

    protected override Expression VisitUnary(UnaryExpression node)
    {
        // Handle negation for not-in: !array.Contains(x.Prop)
        if (node.NodeType == ExpressionType.Not && node.Operand is MethodCallExpression methodCall)
        {
            if (methodCall.Method.Name == "Contains")
            {
                HandleContainsMethod(methodCall, isNegated: true);
                return node;
            }
        }

        return base.VisitUnary(node);
    }

    private void HandleContainsMethod(MethodCallExpression node, bool isNegated)
    {
        // Resolve the (collection, value) operands regardless of which Contains overload the
        // compiler bound to:
        //   - instance method:            list.Contains(value)                 -> node.Object is the collection
        //   - Enumerable.Contains:        Enumerable.Contains(source, value)   -> static, 2 args
        //   - MemoryExtensions.Contains:  arrays bind to the span overload, wrapping the array in
        //                                 an implicit ReadOnlySpan<T> conversion around arg[0]
        Expression? collectionExpr;
        Expression? valueExpr;

        if (node.Object is not null && node.Arguments.Count == 1)
        {
            // Instance method: collection.Contains(value)
            collectionExpr = node.Object;
            valueExpr = node.Arguments[0];
        }
        else if (node.Object is null && node.Arguments.Count >= 2)
        {
            // Static extension: Contains(source, value). Unwrap any implicit span/reference
            // conversion so the source is the underlying array/collection expression.
            collectionExpr = UnwrapConversions(node.Arguments[0]);
            valueExpr = node.Arguments[1];
        }
        else
        {
            return;
        }

        // Case 1: x.Field.Contains(value) -> array-contains (the collection is a document field).
        if (collectionExpr is MemberExpression collectionMember
            && collectionMember.Expression is ParameterExpression)
        {
            var field = CamelCaseHelper.ToCamelCase(collectionMember.Member.Name);
            var value = EvaluateExpression(valueExpr);
            Clauses.Add(new WhereClause(field, "array-contains", value));
            return;
        }

        // Case 2: collection.Contains(x.Field) -> in / not-in (the value is a document field).
        if (valueExpr is MemberExpression valueMember
            && valueMember.Expression is ParameterExpression)
        {
            var field = CamelCaseHelper.ToCamelCase(valueMember.Member.Name);
            var collection = EvaluateExpression(collectionExpr);
            var op = isNegated ? "not-in" : "in";
            Clauses.Add(new WhereClause(field, op, collection));
        }
    }

    // Strips implicit conversions the compiler inserts around a Contains source argument, such as
    // the array -> ReadOnlySpan<T> conversion (op_Implicit) used by MemoryExtensions.Contains, or
    // Convert nodes, so the underlying array/collection expression can be evaluated directly.
    private static Expression UnwrapConversions(Expression expr)
    {
        while (true)
        {
            if (expr is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            {
                expr = unary.Operand;
            }
            else if (expr is MethodCallExpression { Method.Name: "op_Implicit" or "op_Explicit" } conversion
                     && conversion.Arguments.Count == 1)
            {
                expr = conversion.Arguments[0];
            }
            else
            {
                return expr;
            }
        }
    }

    private static (string? memberName, object? value, bool isMember) ExtractMemberAndValue(Expression expr)
    {
        // Direct member access: x.Property
        if (expr is MemberExpression member && member.Expression is ParameterExpression)
        {
            return (member.Member.Name, null, true);
        }

        // Constant value
        if (expr is ConstantExpression constant)
        {
            return (null, constant.Value, false);
        }

        // Captured variable or property access: closure.field or obj.Property
        if (expr is MemberExpression closureMember)
        {
            var value = EvaluateExpression(closureMember);
            return (null, value, false);
        }

        return (null, null, false);
    }

    private static object? EvaluateExpression(Expression expr)
    {
        if (expr is ConstantExpression constant)
            return constant.Value;

        // Compile and execute the expression to get the value
        try
        {
            var lambda = Expression.Lambda(expr);
            var compiled = lambda.Compile();
            return compiled.DynamicInvoke();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to evaluate expression '{expr}'. Ensure the expression is a constant, captured variable, or simple property access.",
                ex);
        }
    }

    private static string GetOperator(ExpressionType nodeType) => nodeType switch
    {
        ExpressionType.Equal => "==",
        ExpressionType.NotEqual => "!=",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        _ => throw new NotSupportedException($"Operator {nodeType} is not supported in Firestore queries")
    };

    private static string GetReversedOperator(ExpressionType nodeType) => nodeType switch
    {
        ExpressionType.Equal => "==",
        ExpressionType.NotEqual => "!=",
        ExpressionType.LessThan => ">",
        ExpressionType.LessThanOrEqual => ">=",
        ExpressionType.GreaterThan => "<",
        ExpressionType.GreaterThanOrEqual => "<=",
        _ => throw new NotSupportedException($"Operator {nodeType} is not supported in Firestore queries")
    };
}

/// <summary>
/// Manages a real-time subscription to a Firestore collection.
/// Thread-safe and properly handles disposal during async startup.
/// </summary>
internal sealed class CollectionSnapshotSubscription<T> : ISnapshotCallback, IDisposable, IAsyncDisposable where T : class
{
    private readonly FirebaseJsInterop _jsInterop;
    private readonly string _path;
    private readonly object? _queryParams;
    private readonly Action<IReadOnlyList<DocumentSnapshot<T>>> _onNext;
    private readonly Action<Exception>? _onError;
    private readonly object _lock = new();
    private DotNetObjectReference<ISnapshotCallback>? _callbackRef;
    private int _subscriptionId;
    private bool _disposed;

    public CollectionSnapshotSubscription(
        FirebaseJsInterop jsInterop,
        string path,
        object? queryParams,
        Action<IReadOnlyList<DocumentSnapshot<T>>> onNext,
        Action<Exception>? onError)
    {
        _jsInterop = jsInterop;
        _path = path;
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
            var result = await _jsInterop.FirestoreSubscribeCollectionAsync(_path, _queryParams, callbackRef);

            lock (_lock)
            {
                // Check if disposed during await
                if (_disposed)
                {
                    // We were disposed while awaiting - clean up the subscription
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
                    // Dispose callback ref on error to prevent memory leak
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
        // Not used for collection subscriptions
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

    /// <summary>
    /// Yield off the JS interop entry stack before user callbacks. Nested Firestore subscriptions
    /// (e.g. workspace fan-out) call back into JS; re-entrant interop can fault Blazor WASM.
    /// </summary>
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
            InvokeSnapshotError(ex);
        }
    }

    private void InvokeSnapshotError(Exception ex)
    {
        try
        {
            _onError?.Invoke(ex);
        }
        catch
        {
            // Listener errors must not fault the WASM runtime.
        }
    }

    public void Dispose()
    {
        if (!TryBeginDispose(out var subscriptionId, out var callbackRef))
        {
            return;
        }

        // WASM cannot block on async teardown (PlatformNotSupportedException on Monitor.Wait).
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
            var result = await _jsInterop.FirestoreUnsubscribeAsync(subscriptionId);
            if (!result.Success && result.Error != null)
            {
                Debug.WriteLine($"[FireBlazor] Failed to unsubscribe from collection {_path}: {result.Error.Code} - {result.Error.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FireBlazor] Error during collection unsubscribe: {ex.Message}");
        }
    }
}
