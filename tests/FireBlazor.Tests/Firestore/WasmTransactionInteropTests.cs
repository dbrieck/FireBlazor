using System.Text.Json.Nodes;
using FireBlazor.Platform.Wasm;

namespace FireBlazor.Tests.Firestore;

/// <summary>
/// End-to-end tests of <see cref="WasmFirestore.TransactionAsync{T}"/> over the real Blazor JS-interop
/// dispatcher (see <see cref="TransactionInteropHarness"/>). They cover the shape that hung expense approval
/// in the browser: the dry run's Exists=false placeholder makes the body bail out before its later reads, and
/// the execute pass then returns a failed <see cref="Result{T}"/>.
/// </summary>
public class WasmTransactionInteropTests
{
    public sealed class Expense
    {
        public string? Status { get; set; }
        public int Version { get; set; }
    }

    public sealed class Journal
    {
        public string? Source { get; set; }
    }

    private const string ExpensePath = "workspaces/ws1/expenses/e1";
    private const string JournalPath = "workspaces/ws1/journalEntries/je-e1";

    private static (TransactionInteropHarness Js, WasmFirestore Firestore) Create(bool expenseExists = true, string status = "submitted")
    {
        var js = new TransactionInteropHarness();
        if (expenseExists)
            js.Documents[ExpensePath] = new JsonObject { ["status"] = status, ["version"] = 3 };
        return (js, new WasmFirestore(new FirebaseJsInterop(js)));
    }

    private static IDocumentReference<Expense> ExpenseRef(WasmFirestore fs) =>
        fs.Collection<Expense>("workspaces/ws1/expenses").Document("e1");

    private static IDocumentReference<Journal> JournalRef(WasmFirestore fs) =>
        fs.Collection<Journal>("workspaces/ws1/journalEntries").Document("je-e1");

    /// <summary>
    /// The expense-approve body: a guarded status flip plus a create-only journal. Returns a failed
    /// <see cref="Result{T}"/> (not an exception) for every business refusal, as ThisProp callers do.
    /// </summary>
    private static Func<ITransaction, Task<Result<Unit>>> ApproveBody(WasmFirestore fs) => async tx =>
    {
        var snap = await tx.GetAsync(ExpenseRef(fs));
        if (!snap.IsSuccess)
            return Result<Unit>.Failure(snap.Error!);
        if (snap.Value is not { Exists: true, Data: { } live })
            return Result<Unit>.Failure(new FirebaseError("not-found", "Expense not found."));
        if (live.Status != "submitted")
            return Result<Unit>.Failure(new FirebaseError("failed-precondition", "Only submitted expenses can be approved."));

        // Guard read the dry run never reaches: its placeholder said the expense does not exist.
        var guard = await tx.GetAsync(JournalRef(fs));
        if (!guard.IsSuccess || guard.Value.Exists)
            return Result<Unit>.Failure(new FirebaseError("failed-precondition", "Journal already posted."));

        tx.Update(ExpenseRef(fs), new { status = "approved", version = live.Version + 1 });
        tx.Set(JournalRef(fs), new Journal { Source = "e1" });
        return Result<Unit>.Success(Unit.Value);
    };

    [Fact]
    public async Task DryRunBailsBeforeGuardRead_ExecutePassReadsGuardTransactionally_AndCommits()
    {
        var (js, fs) = Create();

        var result = await TransactionInteropHarness.AwaitOrHangAsync(fs.TransactionAsync(ApproveBody(fs)));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(result.Value.IsSuccess, result.Value.Error?.ToString());
        Assert.Equal("approved", js.Documents[ExpensePath]["status"]!.GetValue<string>());
        Assert.True(js.Documents.ContainsKey(JournalPath));
        // The guard joined the transaction's read set on the JS side, not a stale out-of-transaction read.
        Assert.Contains(JournalPath, js.TransactionReads);
    }

    [Fact]
    public async Task BodyReturnsFailedResult_CompletesWithThatFailure_AndWritesNothing()
    {
        var (js, fs) = Create(status: "approved");

        var result = await TransactionInteropHarness.AwaitOrHangAsync(fs.TransactionAsync(ApproveBody(fs)));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(result.Value.IsFailure);
        Assert.Equal("failed-precondition", result.Value.Error!.Code);
        Assert.Equal("approved", js.Documents[ExpensePath]["status"]!.GetValue<string>());
        Assert.False(js.Documents.ContainsKey(JournalPath));
    }

    [Fact]
    public async Task GuardRefuses_CompletesWithFailedResult()
    {
        var (js, fs) = Create();
        js.Documents[JournalPath] = new JsonObject { ["source"] = "someone-else" };

        var result = await TransactionInteropHarness.AwaitOrHangAsync(fs.TransactionAsync(ApproveBody(fs)));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("Journal already posted.", result.Value.Error?.Message);
        Assert.Equal("submitted", js.Documents[ExpensePath]["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task CachedPreReaderJs_DryRunBailsBeforeGuardRead_CompletesWithFailedResult_NotHang()
    {
        // fireblazor.js is not fingerprinted: a cached copy can call the one-argument ProcessTransaction.
        var js = new TransactionInteropHarness { PassTransactionReader = false };
        js.Documents[ExpensePath] = new JsonObject { ["status"] = "submitted", ["version"] = 3 };
        var fs = new WasmFirestore(new FirebaseJsInterop(js));

        var result = await TransactionInteropHarness.AwaitOrHangAsync(fs.TransactionAsync(ApproveBody(fs)));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(result.Value.IsFailure);
        Assert.Equal("submitted", js.Documents[ExpensePath]["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task BodyReadsValueOfFailedResult_CompletesWithFailure_NotHang()
    {
        var (_, fs) = Create();

        var result = await TransactionInteropHarness.AwaitOrHangAsync(fs.TransactionAsync<int>(_ =>
            Task.FromResult(Result<int>.Failure(new FirebaseError("not-found", "Expense not found.")).Value)));

        Assert.True(result.IsFailure);
        Assert.Contains("Cannot access Value on failed result", result.Error!.Message);
    }

    [Fact]
    public async Task BodyThrowsInExecutePass_CompletesWithFailure_AndWritesNothing()
    {
        var (js, fs) = Create();

        var result = await TransactionInteropHarness.AwaitOrHangAsync(fs.TransactionAsync<Unit>(async tx =>
        {
            var snap = await tx.GetAsync(ExpenseRef(fs));
            if (snap.Value.Exists)
            {
                tx.Update(ExpenseRef(fs), new { status = "approved" });
                throw new InvalidOperationException("boom");
            }

            return Unit.Value;
        }));

        Assert.True(result.IsFailure);
        Assert.Contains("boom", result.Error!.Message);
        Assert.Equal("submitted", js.Documents[ExpensePath]["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task DryRunReadsNothing_ButExecuteReads_StillRunsAsTransaction()
    {
        var (js, fs) = Create();
        var calls = 0;

        var result = await TransactionInteropHarness.AwaitOrHangAsync(fs.TransactionAsync(async tx =>
        {
            // First invocation (the dry run) bails before any read; later ones read and write.
            if (Interlocked.Increment(ref calls) == 1)
                return Result<Unit>.Failure(new FirebaseError("not-found", "Expense not found."));

            var snap = await tx.GetAsync(ExpenseRef(fs));
            if (snap.Value is not { Exists: true })
                return Result<Unit>.Failure(new FirebaseError("not-found", "Expense not found."));

            tx.Update(ExpenseRef(fs), new { status = "approved" });
            return Result<Unit>.Success(Unit.Value);
        }));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(result.Value.IsSuccess, result.Value.Error?.ToString());
        Assert.Equal("approved", js.Documents[ExpensePath]["status"]!.GetValue<string>());
        Assert.Contains(ExpensePath, js.TransactionReads);
    }

    [Fact]
    public async Task HappyPath_AllReadsKnownToDryRun_Commits()
    {
        var (js, fs) = Create();

        var result = await TransactionInteropHarness.AwaitOrHangAsync(fs.TransactionAsync(async tx =>
        {
            var snap = await tx.GetAsync(ExpenseRef(fs));
            var guard = await tx.GetAsync(JournalRef(fs));
            if (snap.Value is not { Exists: true } || guard.Value.Exists)
                return 0;
            tx.Update(ExpenseRef(fs), new { status = "approved" });
            return 42;
        }));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(42, result.Value);
        Assert.Equal("approved", js.Documents[ExpensePath]["status"]!.GetValue<string>());
        Assert.Equal(1, js.TransactionAttempts);
    }
}
