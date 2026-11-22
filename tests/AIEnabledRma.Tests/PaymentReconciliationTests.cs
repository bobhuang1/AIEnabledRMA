using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Domain.Rma;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIEnabledRma.Tests;

/// <summary>
/// Reconciliation against the bundled fake gateway, which is a real implementation of the payment
/// boundary: it holds authorisations, captures and voids in memory. That lets a test move the money
/// at the provider and leave the application's row untouched, which is the state a webhook that was
/// never delivered leaves behind — and the case nothing else in this repository can produce.
/// </summary>
public sealed class PaymentReconciliationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _database = new("rma_reconcile");

    private readonly AIEnabledRma.Ai.FakePaymentGateway _gateway =
        new(NullLogger<AIEnabledRma.Ai.FakePaymentGateway>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();

        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync(Ct);
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task A_capture_the_provider_holds_and_this_database_never_heard_of_is_filed_for_review()
    {
        var transactionId = await AuthoriseAsync();
        await SeedAsync(RmaRequestStatus.Approved, transactionId, Now.AddHours(-7));

        // The money moved at the provider. No webhook arrived, so the row still says Approved.
        var captured = await _gateway.CaptureAsync(transactionId, Ct);
        Assert.True(captured.IsSuccess);

        var report = await ReconcileAsync();

        Assert.Equal(1, report.Checked);
        Assert.Equal(0, report.Agreed);
        Assert.Empty(report.Unanswered);

        var disagreement = Assert.Single(report.Disagreements);
        Assert.Equal(transactionId, disagreement.TransactionId);
        Assert.Equal(PaymentStatus.Captured, disagreement.Provider);
        Assert.Equal(PaymentStatus.Authorized, disagreement.Expected);

        await using var check = _database.CreateContext();
        var item = await check.ReviewItems.SingleAsync(Ct);

        Assert.Equal(ReviewSources.PaymentMismatch, item.Source);
        Assert.Equal("PaymentMismatch:Captured", item.Reason);
        Assert.Contains(transactionId, item.Explanation!, StringComparison.Ordinal);
        Assert.Contains("webhook", item.Explanation!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReviewStatuses.Open, item.Status);

        // The run looks; it does not move the return. A reconciliation that rewrote statuses would
        // be a second, unlogged operator.
        var stored = await check.RmaRequests.SingleAsync(Ct);
        Assert.Equal(RmaRequestStatus.Approved, stored.Status);
    }

    [Fact]
    public async Task An_authorisation_that_still_matches_the_provider_files_nothing()
    {
        var transactionId = await AuthoriseAsync();
        await SeedAsync(RmaRequestStatus.AwaitingShipment, transactionId, Now.AddDays(-3));

        var report = await ReconcileAsync();

        Assert.Equal(1, report.Checked);
        Assert.Equal(1, report.Agreed);
        Assert.Empty(report.Disagreements);

        await using var check = _database.CreateContext();
        Assert.Equal(0, await check.ReviewItems.CountAsync(Ct));
    }

    [Fact]
    public async Task A_return_the_provider_has_voided_is_filed_while_the_row_still_looks_active()
    {
        var transactionId = await AuthoriseAsync();
        await SeedAsync(RmaRequestStatus.AwaitingShipment, transactionId, Now.AddHours(-9));

        Assert.True((await _gateway.VoidAsync(transactionId, Ct)).IsSuccess);

        var report = await ReconcileAsync();

        var disagreement = Assert.Single(report.Disagreements);
        Assert.Equal(PaymentStatus.Voided, disagreement.Provider);
        Assert.Equal(PaymentStatus.Authorized, disagreement.Expected);

        await using var check = _database.CreateContext();
        var item = await check.ReviewItems.SingleAsync(Ct);
        Assert.Equal("PaymentMismatch:Voided", item.Reason);
    }

    [Fact]
    public async Task A_transaction_the_provider_cannot_answer_for_is_reported_and_not_filed()
    {
        // Nothing was ever authorised under this id: the gateway refuses to answer, which is not a
        // disagreement and must not put a review item in the queue.
        await SeedAsync(RmaRequestStatus.Approved, "pi_never_seen", Now.AddDays(-2));

        var report = await ReconcileAsync();

        Assert.Equal(1, report.Checked);
        Assert.Empty(report.Disagreements);
        Assert.Single(report.Unanswered);

        await using var check = _database.CreateContext();
        Assert.Equal(0, await check.ReviewItems.CountAsync(Ct));
    }

    [Fact]
    public async Task A_row_written_inside_the_window_is_not_asked_about()
    {
        var transactionId = await AuthoriseAsync();
        await SeedAsync(RmaRequestStatus.Approved, transactionId, Now.AddMinutes(-5));

        var report = await ReconcileAsync();

        // A delivery that arrived writes the row, so a row nobody has touched is the shape of one
        // that never arrived. Recent rows are somebody else's business.
        Assert.Equal(0, report.Checked);
        Assert.Empty(report.Disagreements);
    }

    [Fact]
    public async Task A_second_run_does_not_file_the_same_disagreement_again()
    {
        var transactionId = await AuthoriseAsync();
        await SeedAsync(RmaRequestStatus.Approved, transactionId, Now.AddHours(-7));
        await _gateway.CaptureAsync(transactionId, Ct);

        await ReconcileAsync();

        var second = await ReconcileAsync();

        // Still reported — the drift has not gone away — but not filed twice: an overnight run would
        // otherwise bury the queue in identical items.
        Assert.Single(second.Disagreements);

        await using var check = _database.CreateContext();
        Assert.Equal(1, await check.ReviewItems.CountAsync(Ct));
    }

    [Fact]
    public async Task A_resolved_item_allows_the_drift_to_be_filed_again()
    {
        var transactionId = await AuthoriseAsync();
        await SeedAsync(RmaRequestStatus.Approved, transactionId, Now.AddHours(-7));
        await _gateway.CaptureAsync(transactionId, Ct);

        await ReconcileAsync();

        await using (var resolve = _database.CreateContext())
        {
            var item = await resolve.ReviewItems.SingleAsync(Ct);
            item.Status = ReviewStatuses.Resolved;
            item.ResolvedBy = "operator-demo";
            item.ResolvedAtUtc = Now;
            item.Resolution = "checked the dashboard, capture is expected";
            await resolve.SaveChangesAsync(Ct);
        }

        await ReconcileAsync();

        await using var check = _database.CreateContext();
        Assert.Equal(2, await check.ReviewItems.CountAsync(Ct));
    }

    private async Task<string> AuthoriseAsync()
    {
        var receipt = await _gateway.AuthorizeAsync(
            new PaymentAuthorizationRequest
            {
                RmaNumber = "RMA-2026-" + Guid.NewGuid().ToString("N")[..5],
                Amount = new Money(69m, "EUR"),
                Purpose = PaymentPurpose.Deposit,
                CaptureMode = PaymentCaptureMode.ManualLater,
                CustomerEmail = "customer@example.test",
            },
            Ct);

        Assert.True(receipt.IsSuccess);

        return receipt.Value!.TransactionId;
    }

    private async Task SeedAsync(
        RmaRequestStatus status,
        string transactionId,
        DateTimeOffset updatedAtUtc)
    {
        await using var db = _database.CreateContext();

        db.RmaRequests.Add(new RmaRequest
        {
            Id = Guid.NewGuid(),
            RmaNumber = "RMA-2026-" + Guid.NewGuid().ToString("N")[..5],
            Status = status,
            Kind = RmaRequestKind.PaidRepair,
            CreatedAtUtc = updatedAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            PaymentTransactionId = transactionId,
            DepositAmount = 69m,
            CurrencyCode = "EUR",
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<PaymentReconciliationReport> ReconcileAsync(TimeSpan? staleAfter = null)
    {
        await using var db = _database.CreateContext();

        var reconciler = new PaymentReconciler(
            new RmaRepository(db),
            _gateway,
            new EfOperationsRepository(db),
            new DirectReviewQueue(db),
            new FakeClock(Now),
            NullLogger<PaymentReconciler>.Instance);

        return await reconciler.RunAsync(
            staleAfter ?? PaymentReconciler.DefaultStaleAfter,
            PaymentReconciler.DefaultTake,
            Ct);
    }

    /// <summary>
    /// Files straight into the context the run is already using. The production queue opens its own
    /// scope, which a test has no host to provide; what matters here is that the item is written.
    /// </summary>
    private sealed class DirectReviewQueue(RmaDbContext db) : IReviewQueue
    {
        public async Task AddAsync(ReviewItem item, CancellationToken cancellationToken)
        {
            db.ReviewItems.Add(item);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
