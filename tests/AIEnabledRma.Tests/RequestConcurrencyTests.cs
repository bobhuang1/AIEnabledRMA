using AIEnabledRma.Data;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// Two writers on one row. The claim under test is the database's: a save built from a read that
/// is already out of date must be refused, and the audit trail must record the loser as a refusal
/// rather than as an action that happened.
/// </summary>
/// <remarks>
/// Each test keeps a stale entity in a second context, which is what a second browser session has
/// once the first one has saved: the repository hands back the instance that context already
/// tracks, so the service acts on the old copy. Re-reading instead would only re-prove the status
/// guard and never reach the concurrency token, which is exactly the hole these tests exist for.
/// </remarks>
public sealed class RequestConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stale_operator_action_is_refused_and_audited_as_a_refusal()
    {
        await using var database = new LiveDatabase("rma_concurrency");
        await database.InitializeAsync();

        var request = Request(RmaRequestStatus.AwaitingShipment);

        await using (var seed = database.CreateContext())
        {
            await seed.Database.MigrateAsync(Ct);
            seed.RmaRequests.Add(request);
            await seed.SaveChangesAsync(Ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();

        // Operator two's page was rendered before operator one acted.
        Assert.NotNull(await new RmaRepository(second).GetByIdAsync(request.Id, Ct));

        var received = await OperatorServiceFor(first).MarkReceivedAsync(request.Id, "operator-one", Ct);
        Assert.True(received.Succeeded);

        var voided = await OperatorServiceFor(second).VoidAsync(
            request.Id, "operator-two", "customer changed their mind", Ct);

        // Refused, and told why: "voided" would have been a lie about a row in Received.
        Assert.False(voided.Succeeded);
        Assert.Contains("changed", voided.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = database.CreateContext();

        var stored = await check.RmaRequests.SingleAsync(r => r.Id == request.Id, Ct);

        // The first write stands. Without the token this row reads Cancelled and both operators
        // were told their action worked.
        Assert.Equal(RmaRequestStatus.Received, stored.Status);
        Assert.Null(stored.CancellationReason);

        var actions = await check.OperatorActions.ToListAsync(Ct);

        // Two rows, not three: the rolled-back action is the one that was flipped to a refusal,
        // rather than a second row being added beside it.
        Assert.Equal(2, actions.Count);
        Assert.True(actions.Single(a => a.Action == "receive").Succeeded);
        Assert.False(actions.Single(a => a.Action == "void").Succeeded);
    }

    [Fact]
    public async Task A_stale_capture_is_refused_before_it_reaches_the_provider()
    {
        await using var database = new LiveDatabase("rma_concurrency");
        await database.InitializeAsync();

        var request = Request(RmaRequestStatus.Received);
        request.PaymentTransactionId = "pi_stale_capture";
        request.DepositAmount = 69m;
        request.CurrencyCode = "EUR";

        await using (var seed = database.CreateContext())
        {
            await seed.Database.MigrateAsync(Ct);
            seed.RmaRequests.Add(request);
            await seed.SaveChangesAsync(Ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();

        // One provider, as two browser sessions of one deployment share. What matters is what it was
        // asked to do, not which service object asked.
        var gateway = new FakePaymentGateway();

        // Both operators have the capture form open on an item in Received.
        Assert.NotNull(await new RmaRepository(second).GetByIdAsync(request.Id, Ct));

        var captured = await OperatorServiceFor(first, gateway).CaptureAsync(
            request.Id, "operator-one", "screen replaced under warranty", Ct);
        Assert.True(captured.Succeeded);

        // The live capture did reach the provider, so what follows is about the stale one rather than
        // about a gateway that was never wired up.
        Assert.Equal(new[] { "pi_stale_capture" }, gateway.Captures);

        // The second operator submits the same form. Its copy still says Received, so the status
        // guard cannot refuse this one: only the version check can — and it now runs under the row
        // lock, before the provider is told anything.
        var stale = await OperatorServiceFor(second, gateway).CaptureAsync(
            request.Id, "operator-two", "screen replaced under warranty", Ct);

        Assert.False(stale.Succeeded);
        Assert.Contains("nothing was saved", stale.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Captured", stale.Message, StringComparison.Ordinal);

        // One capture at the provider for one return. Before the guard the second form charged the
        // card and was only refused at the row write, which is money moved for nothing.
        Assert.Single(gateway.Captures);

        await using var check = database.CreateContext();
        var stored = await check.RmaRequests.SingleAsync(r => r.Id == request.Id, Ct);
        Assert.Equal(RmaRequestStatus.InRepair, stored.Status);

        var captures = await check.OperatorActions
            .Where(a => a.Action == "capture")
            .ToListAsync(Ct);

        // Two attempts, one success: the stale one is recorded as the refusal it was, rather than
        // as a second capture that never happened.
        Assert.Equal(2, captures.Count);
        Assert.Single(captures, a => a.Succeeded);
        var refused = Assert.Single(captures, a => !a.Succeeded);
        Assert.Contains("changed since it was read", refused.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The capture that loses the race must not move the row to InRepair. The both-capture test above
    /// cannot show that: there both writers want InRepair, so a lost write and a landed one leave the
    /// same status and only the ledger tells them apart. Here the competing write is a void, so the row
    /// ends Cancelled and a stale capture that wrote through would visibly flip it back to InRepair —
    /// while the provider, already called, would be holding money for a cancelled return.
    /// </summary>
    [Fact]
    public async Task A_capture_whose_row_was_cancelled_underneath_it_is_refused_and_leaves_the_row_cancelled()
    {
        await using var database = new LiveDatabase("rma_concurrency");
        await database.InitializeAsync();

        var request = Request(RmaRequestStatus.Received);
        request.PaymentTransactionId = "pi_cancelled_capture";
        request.DepositAmount = 69m;
        request.CurrencyCode = "EUR";

        await using (var seed = database.CreateContext())
        {
            await seed.Database.MigrateAsync(Ct);
            seed.RmaRequests.Add(request);
            await seed.SaveChangesAsync(Ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();

        var gateway = new FakePaymentGateway();

        // Both operators have the item open: one on the capture form, one on the cancel panel.
        Assert.NotNull(await new RmaRepository(second).GetByIdAsync(request.Id, Ct));

        var voided = await OperatorServiceFor(first, gateway).VoidAsync(
            request.Id, "operator-one", "customer withdrew the repair", Ct);
        Assert.True(voided.Succeeded);

        // The stale capture. Its copy still says Received, so the status guard passes; the version
        // check under the lock refuses it, and the provider is never asked to take the money.
        var stale = await OperatorServiceFor(second, gateway).CaptureAsync(
            request.Id, "operator-two", "screen replaced under warranty", Ct);

        Assert.False(stale.Succeeded);
        Assert.Contains("nothing was saved", stale.Message, StringComparison.Ordinal);
        Assert.Empty(gateway.Captures);

        await using var check = database.CreateContext();
        var stored = await check.RmaRequests.SingleAsync(r => r.Id == request.Id, Ct);

        // The void stands. Without the token this row reads InRepair while its cancellation reason
        // still names a withdrawal, which is the state nobody would notice until the customer asked
        // why a cancelled return was being repaired.
        Assert.Equal(RmaRequestStatus.Cancelled, stored.Status);
        Assert.NotEqual(RmaRequestStatus.InRepair, stored.Status);
        Assert.Contains("customer withdrew the repair", stored.CancellationReason, StringComparison.Ordinal);

        var actions = await check.OperatorActions.ToListAsync(Ct);

        // One success, and it is the void. The capture appears once, as the refusal it was.
        Assert.Equal(2, actions.Count);
        Assert.True(actions.Single(a => a.Action == "void").Succeeded);
        var refused = Assert.Single(actions, a => a.Action == "capture");
        Assert.False(refused.Succeeded);
        Assert.DoesNotContain(actions, a => a.Action == "capture" && a.Succeeded);
        Assert.Contains("changed since it was read", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_confirmation_from_a_stale_page_is_refused_and_reports_the_stored_status()
    {
        await using var database = new LiveDatabase("rma_concurrency");
        await database.InitializeAsync();

        var request = Request(RmaRequestStatus.Approved);

        await using (var seed = database.CreateContext())
        {
            await seed.Database.MigrateAsync(Ct);
            seed.RmaRequests.Add(request);
            await seed.SaveChangesAsync(Ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();

        // The second tab still shows the confirmation page for an Approved return.
        Assert.NotNull(await new RmaRepository(second).GetByIdAsync(request.Id, Ct));

        var confirmed = await WorkflowFor(first).ConfirmAsync(request.Id, Ct);
        Assert.Equal(StepOutcome.Completed, confirmed.Outcome);

        // The double-submitted confirmation. Both callers read Approved, so the status guard
        // passes for both and only the write can tell them apart.
        var again = await WorkflowFor(second).ConfirmAsync(request.Id, Ct);

        Assert.Equal(StepOutcome.InvalidTransition, again.Outcome);
        Assert.Contains("changed", again.Message, StringComparison.OrdinalIgnoreCase);

        // One refusal path, shared with every other step: the message names the step the helper was
        // given. A bespoke confirmation wording reappearing beside it — the duplicate this replaced —
        // fails here rather than living on as a second message nobody reads.
        Assert.Contains("confirmation step", again.Message, StringComparison.Ordinal);

        // Reported from the row that is actually stored, not from the stale page.
        Assert.Equal(RmaRequestStatus.AwaitingShipment, again.Status);

        await using var check = database.CreateContext();
        var stored = await check.RmaRequests.SingleAsync(r => r.Id == request.Id, Ct);

        Assert.Equal(RmaRequestStatus.AwaitingShipment, stored.Status);
        Assert.Equal(Now, stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task Two_operators_resolving_one_review_item_leave_the_first_resolution_standing()
    {
        await using var database = new LiveDatabase("rma_concurrency");
        await database.InitializeAsync();

        var item = new ReviewItem
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = Now,
            Source = ReviewSources.PaymentFailed,
            Reason = "authorisation refused",
        };

        await using (var seed = database.CreateContext())
        {
            await seed.Database.MigrateAsync(Ct);
            seed.ReviewItems.Add(item);
            await seed.SaveChangesAsync(Ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();

        // Both operators opened the queue while the item was still open. The status guard reads the
        // copy this context tracks, so for the second one it still says Open.
        Assert.NotNull(await new EfOperationsRepository(second).GetReviewItemAsync(item.Id, Ct));

        var resolved = await OperatorServiceFor(first).ResolveReviewItemAsync(
            item.Id, "operator-one", "refunded by hand", Ct);
        Assert.True(resolved.Succeeded);

        // The second submission. Without a token on review_items both are told "Resolved." and the
        // second silently replaces the first operator's name and explanation.
        var stale = await OperatorServiceFor(second).ResolveReviewItemAsync(
            item.Id, "operator-two", "nothing to refund", Ct);

        Assert.False(stale.Succeeded);
        Assert.Contains("changed", stale.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = database.CreateContext();

        var stored = await check.ReviewItems.SingleAsync(r => r.Id == item.Id, Ct);

        // The first resolution stands, so the record says who actually decided this and why.
        Assert.Equal(ReviewStatuses.Resolved, stored.Status);
        Assert.Equal("operator-one", stored.ResolvedBy);
        Assert.Equal("refunded by hand", stored.Resolution);

        var actions = await check.OperatorActions
            .Where(a => a.Action == "resolve-review")
            .ToListAsync(Ct);

        // Two attempts, one success: the stale one is the refusal it was, not a second resolution.
        Assert.Equal(2, actions.Count);
        Assert.Single(actions, a => a.Succeeded);
        var refused = Assert.Single(actions, a => !a.Succeeded);
        Assert.Contains("changed since it was read", refused.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The void is the other money transition: it releases an authorisation at the provider before it
    /// writes the row. A void built from a page that still said Received, sent after another operator
    /// captured, would hand the customer the fee back while the return moved to repair.
    /// </summary>
    [Fact]
    public async Task A_stale_void_never_releases_the_authorisation_another_operator_captured()
    {
        await using var database = new LiveDatabase("rma_concurrency");
        await database.InitializeAsync();

        var request = Request(RmaRequestStatus.Received);
        request.PaymentTransactionId = "pi_captured_before_void";
        request.DepositAmount = 69m;
        request.CurrencyCode = "EUR";

        await using (var seed = database.CreateContext())
        {
            await seed.Database.MigrateAsync(Ct);
            seed.RmaRequests.Add(request);
            await seed.SaveChangesAsync(Ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();

        var gateway = new FakePaymentGateway();

        // One operator captures the fee; the other still has the cancel panel open on Received.
        Assert.NotNull(await new RmaRepository(second).GetByIdAsync(request.Id, Ct));

        var captured = await OperatorServiceFor(first, gateway).CaptureAsync(
            request.Id, "operator-one", "screen replaced under warranty", Ct);
        Assert.True(captured.Succeeded);

        var stale = await OperatorServiceFor(second, gateway).VoidAsync(
            request.Id, "operator-two", "customer withdrew the repair", Ct);

        Assert.False(stale.Succeeded);
        Assert.Contains("nothing was saved", stale.Message, StringComparison.Ordinal);

        // The authorisation was never released: the capture the other operator took still stands at
        // the provider, which is the only place that can undo the charge.
        Assert.Empty(gateway.Voids);
        Assert.Equal(new[] { "pi_captured_before_void" }, gateway.Captures);

        await using var check = database.CreateContext();
        var stored = await check.RmaRequests.SingleAsync(r => r.Id == request.Id, Ct);

        Assert.Equal(RmaRequestStatus.InRepair, stored.Status);
        Assert.Null(stored.CancellationReason);

        var actions = await check.OperatorActions.ToListAsync(Ct);

        Assert.Equal(2, actions.Count);
        Assert.True(actions.Single(a => a.Action == "capture").Succeeded);
        Assert.False(actions.Single(a => a.Action == "void").Succeeded);
    }

    /// <summary>
    /// The other half of the same claim: a void from a current page still reaches the provider. A
    /// guard that refused everything would pass the stale tests above and quietly break the release.
    /// </summary>
    [Fact]
    public async Task A_current_void_releases_the_authorisation()
    {
        await using var database = new LiveDatabase("rma_concurrency");
        await database.InitializeAsync();

        var request = Request(RmaRequestStatus.Received);
        request.PaymentTransactionId = "pi_current_void";
        request.DepositAmount = 69m;
        request.CurrencyCode = "EUR";

        await using (var seed = database.CreateContext())
        {
            await seed.Database.MigrateAsync(Ct);
            seed.RmaRequests.Add(request);
            await seed.SaveChangesAsync(Ct);
        }

        var gateway = new FakePaymentGateway();

        await using var context = database.CreateContext();
        var voided = await OperatorServiceFor(context, gateway).VoidAsync(
            request.Id, "operator-one", "customer withdrew the repair", Ct);

        Assert.True(voided.Succeeded);
        Assert.Equal(new[] { "pi_current_void" }, gateway.Voids);

        await using var check = database.CreateContext();
        var stored = await check.RmaRequests.SingleAsync(r => r.Id == request.Id, Ct);

        Assert.Equal(RmaRequestStatus.Cancelled, stored.Status);
        Assert.Contains("customer withdrew the repair", stored.CancellationReason, StringComparison.Ordinal);
    }

    private static RmaRequest Request(RmaRequestStatus status) => new()
    {
        Id = Guid.NewGuid(),
        RmaNumber = "RMA-2026-" + Guid.NewGuid().ToString("N")[..5],
        Status = status,
        Kind = RmaRequestKind.Warranty,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    private static OperatorService OperatorServiceFor(RmaDbContext db, FakePaymentGateway? payments = null) => new(
        new RmaRepository(db),
        new EfOperationsRepository(db),
        payments ?? new FakePaymentGateway(),
        new EfUnitOfWork(db),
        new FakeClock(Now),
        NullLogger<OperatorService>.Instance);

    private static RmaWorkflow WorkflowFor(RmaDbContext db) => new(
        new RmaRepository(db),
        new DeviceRepository(db, Options.Create(new DeviceLookupOptions())),
        new RmaPolicyEvaluator(Options.Create(new RmaPolicyOptions())),
        new FakePaymentGateway(),
        new EfUnitOfWork(db),
        Options.Create(new RmaPolicyOptions()),
        new FakeClock(Now),
        NullLogger<RmaWorkflow>.Instance);
}
