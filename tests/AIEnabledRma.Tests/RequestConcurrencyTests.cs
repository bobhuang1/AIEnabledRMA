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
    public async Task A_stale_capture_is_refused_and_says_the_provider_may_already_hold_it()
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

        // Both operators have the capture form open on an item in Received.
        Assert.NotNull(await new RmaRepository(second).GetByIdAsync(request.Id, Ct));

        var captured = await OperatorServiceFor(first).CaptureAsync(
            request.Id, "operator-one", "screen replaced under warranty", Ct);
        Assert.True(captured.Succeeded);

        // The second operator submits the same form. Its status guard still reads Received, so the
        // guard cannot refuse this one: only the token can. The provider was already called before
        // the write, which is why the refusal must not claim the money stayed put.
        var stale = await OperatorServiceFor(second).CaptureAsync(
            request.Id, "operator-two", "screen replaced under warranty", Ct);

        Assert.False(stale.Succeeded);
        Assert.Contains("payment provider may already hold", stale.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Captured", stale.Message, StringComparison.Ordinal);

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

        // Reported from the row that is actually stored, not from the stale page.
        Assert.Equal(RmaRequestStatus.AwaitingShipment, again.Status);

        await using var check = database.CreateContext();
        var stored = await check.RmaRequests.SingleAsync(r => r.Id == request.Id, Ct);

        Assert.Equal(RmaRequestStatus.AwaitingShipment, stored.Status);
        Assert.Equal(Now, stored.UpdatedAtUtc);
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

    private static OperatorService OperatorServiceFor(RmaDbContext db) => new(
        new RmaRepository(db),
        new EfOperationsRepository(db),
        new FakePaymentGateway(),
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
