using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Domain.Rma;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIEnabledRma.Tests;

/// <summary>
/// The audit against the bundled fake gateway, which holds authorisations, captures and voids in
/// memory. That makes it possible to move the money at the provider and leave the application's row
/// alone — the state a capture whose row write lost the race leaves behind, and the one no other
/// command in this repository looks at, because reconciliation skips a row that was just written.
/// </summary>
public sealed class PaymentAuditTests : IAsyncLifetime
{
    private readonly LiveDatabase _database = new("rma_audit");

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
    public async Task A_capture_the_provider_took_and_the_row_never_recorded_is_reported_as_provider_only()
    {
        var transactionId = await AuthoriseAsync();
        var requestId = await SeedAsync(RmaRequestStatus.Received, transactionId);

        // The operator captured, the provider took the money, and the write that would have recorded
        // it lost the row race: the return still reads Received, and it was written a moment ago.
        Assert.True((await _gateway.CaptureAsync(transactionId, Ct)).IsSuccess);

        var report = await AuditAsync();

        Assert.Equal(1, report.Checked);
        Assert.Equal(0, report.InStep);
        Assert.False(report.More);
        Assert.Empty(report.Unanswered);

        var drift = Assert.Single(report.Drift);
        Assert.Equal(PaymentDriftDirection.ProviderOnly, drift.Direction);
        Assert.Equal(requestId, drift.RmaRequestId);
        Assert.Equal(transactionId, drift.TransactionId);
        Assert.Equal(PaymentStatus.Captured, drift.Provider);
        Assert.Equal(PaymentStatus.Authorized, drift.Expected);
        Assert.Equal("Received", drift.LocalStatus);

        // The audit looks and does not touch: nothing was filed, and the row still says what the
        // operator saw, so whoever runs this can still decide which side is wrong.
        await using var check = _database.CreateContext();
        Assert.Equal(0, await check.ReviewItems.CountAsync(Ct));

        var stored = await check.RmaRequests.SingleAsync(r => r.Id == requestId, Ct);
        Assert.Equal(RmaRequestStatus.Received, stored.Status);
    }

    [Fact]
    public async Task A_capture_the_row_recorded_and_the_provider_never_took_is_reported_row_only()
    {
        // The return moved on as if the deposit had been captured; the provider still holds only an
        // authorisation, so the money was never taken. This is the direction that tells a customer
        // they paid when they did not.
        var transactionId = await AuthoriseAsync();
        await SeedAsync(RmaRequestStatus.InRepair, transactionId);

        var report = await AuditAsync();

        var drift = Assert.Single(report.Drift);
        Assert.Equal(PaymentDriftDirection.RowOnly, drift.Direction);
        Assert.Equal(PaymentStatus.Captured, drift.Expected);
        Assert.Equal(PaymentStatus.Authorized, drift.Provider);
        Assert.Equal("InRepair", drift.LocalStatus);
    }

    [Fact]
    public async Task A_void_here_and_a_capture_there_is_a_mismatch_rather_than_a_missing_write()
    {
        // Both sides hold a terminal state, so neither can be called the stale copy of the other.
        var transactionId = await AuthoriseAsync();
        await SeedAsync(RmaRequestStatus.Cancelled, transactionId);

        Assert.True((await _gateway.CaptureAsync(transactionId, Ct)).IsSuccess);

        var report = await AuditAsync();

        var drift = Assert.Single(report.Drift);
        Assert.Equal(PaymentDriftDirection.Mismatched, drift.Direction);
        Assert.Equal(PaymentStatus.Voided, drift.Expected);
        Assert.Equal(PaymentStatus.Captured, drift.Provider);
    }

    [Fact]
    public async Task A_return_that_agrees_is_counted_and_a_return_without_a_transaction_is_not_asked_about()
    {
        var captured = await AuthoriseAsync();
        Assert.True((await _gateway.CaptureAsync(captured, Ct)).IsSuccess);
        await SeedAsync(RmaRequestStatus.InRepair, captured);

        // No transaction: nothing to ask the provider, and no line in the report.
        await SeedAsync(RmaRequestStatus.Approved, transactionId: null);

        var report = await AuditAsync();

        Assert.Equal(1, report.Checked);
        Assert.Equal(1, report.InStep);
        Assert.Empty(report.Drift);
        Assert.Empty(report.Unanswered);
    }

    [Fact]
    public async Task A_transaction_the_provider_cannot_answer_for_is_unanswered_rather_than_drift()
    {
        await SeedAsync(RmaRequestStatus.Approved, "pi_never_seen");

        var report = await AuditAsync();

        Assert.Equal(1, report.Checked);
        Assert.Equal(0, report.InStep);
        Assert.Empty(report.Drift);

        var unanswered = Assert.Single(report.Unanswered);
        Assert.Contains("pi_never_seen", unanswered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bounded_run_says_it_cut_the_set_short_and_an_unbounded_one_does_not()
    {
        for (var i = 0; i < 3; i++)
        {
            var transactionId = await AuthoriseAsync();
            await SeedAsync(RmaRequestStatus.Approved, transactionId);
        }

        var bounded = await AuditAsync(take: 2);

        Assert.Equal(2, bounded.Checked);
        Assert.True(bounded.More);
        Assert.Equal(2, bounded.InStep);
        Assert.Empty(bounded.Drift);

        var everything = await AuditAsync(take: null);

        Assert.Equal(3, everything.Checked);
        Assert.False(everything.More);
        Assert.Equal(3, everything.InStep);
    }

    /// <summary>
    /// Both sides hold a capture and the sums differ: the row recorded a capture of the wrong amount,
    /// which is a state comparison's blind spot and the reason the audit compares the money too.
    /// </summary>
    [Fact]
    public async Task A_capture_for_a_different_amount_is_drift_even_though_both_say_captured()
    {
        var transactionId = await AuthoriseAsync();
        await SeedAsync(RmaRequestStatus.InRepair, transactionId, deposit: 70m);

        Assert.True((await _gateway.CaptureAsync(transactionId, Ct)).IsSuccess);

        var report = await AuditAsync();

        Assert.Equal(0, report.InStep);

        var drift = Assert.Single(report.Drift);
        Assert.Equal(PaymentDriftDirection.Mismatched, drift.Direction);
        Assert.Equal(PaymentStatus.Captured, drift.Expected);
        Assert.Equal(PaymentStatus.Captured, drift.Provider);
        Assert.Equal(new Money(70m, "EUR"), drift.ExpectedAmount);
        Assert.Equal(new Money(69m, "EUR"), drift.ProviderAmount);
    }

    private async Task<PaymentAuditReport> AuditAsync(int? take = null)
    {
        await using var db = _database.CreateContext();

        var auditor = new PaymentAuditor(
            new RmaRepository(db),
            _gateway,
            NullLogger<PaymentAuditor>.Instance);

        return await auditor.RunAsync(take, Ct);
    }

    /// <summary>An authorisation at the gateway, which is what a return's transaction id points at.</summary>
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

    private async Task<Guid> SeedAsync(RmaRequestStatus status, string? transactionId, decimal deposit = 69m)
    {
        await using var db = _database.CreateContext();

        var request = new RmaRequest
        {
            Id = Guid.NewGuid(),
            RmaNumber = "RMA-2026-" + Guid.NewGuid().ToString("N")[..5],
            Status = status,
            Kind = RmaRequestKind.PaidRepair,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            PaymentTransactionId = transactionId,
            DepositAmount = transactionId is null ? 0m : deposit,
            CurrencyCode = "EUR",
        };

        db.RmaRequests.Add(request);
        await db.SaveChangesAsync(Ct);

        return request.Id;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
}
