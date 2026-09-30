using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Domain.Triage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// The workflow is the only path to a real return, so the tests here are about the order
/// things happen in and about what the AI is and is not allowed to do. The AI may annotate a
/// request and may exit early as "resolved"; it may never move a request into a state that
/// authorises money or a replacement.
/// </summary>
public sealed class RmaWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static RmaPolicyOptions DefaultOptions() => new()
    {
        GracePeriodDays = 30,
        DefaultRepairFee = 99m,
        RepairPrices =
        [
            new ProductRepairPrice { Sku = "AX-200", RepairFee = 149m },
            new ProductRepairPrice { Sku = "AX-400", RepairFee = 69m },
        ],
        Tiers =
        [
            new WarrantyTierPolicy { Name = "standard", IsCovered = true },
            new WarrantyTierPolicy { Name = "no-service", IsCovered = false },
        ],
    };

    private static (RmaWorkflow Workflow, FakeRmaRepository Rmas, FakeDeviceRepository Devices,
        FakePaymentGateway Payments, RecordingUnitOfWork Commits) Build(IEligibilityService? eligibility = null)
    {
        var rmas = new FakeRmaRepository();
        var devices = new FakeDeviceRepository();
        var payments = new FakePaymentGateway();
        // The real host commits through IUnitOfWork after each step. A fake that counts the
        // commits lets a test prove a step actually persisted, rather than only mutated an
        // in-memory object that would have vanished at the end of the request in production.
        var unitOfWork = new RecordingUnitOfWork(rmas, devices);

        var workflow = new RmaWorkflow(
            rmas,
            devices,
            eligibility ?? new RmaPolicyEvaluator(Microsoft.Extensions.Options.Options.Create(DefaultOptions())),
            payments,
            unitOfWork,
            Microsoft.Extensions.Options.Options.Create(DefaultOptions()),
            new FakeClock(Now),
            NullLogger<RmaWorkflow>.Instance);

        return (workflow, rmas, devices, payments, unitOfWork);
    }

    [Fact]
    public async Task Every_state_changing_step_commits_its_writes()
    {
        var (workflow, rmas, devices, _, commits) = Build();
        var device = TestData.Device(serial: "AX-1");
        devices.Devices.Add(device);
        devices.LookupResult = new DeviceLookupResult { Device = device };

        var started = await workflow.StartAsync(
            new StartRmaCommand
            {
                Devices = [Found(device)],
                Today = Today,
                CustomerId = Guid.NewGuid(),
            },
            TestContext.Current.CancellationToken);

        // Without a commit the row exists only in the change tracker and is lost when the
        // request scope is disposed, so the customer gets a reference for a return that no
        // longer exists.
        Assert.Equal(1, commits.CommitCount);

        var rmaId = started.RmaId;
        Assert.NotNull(rmaId);
        Assert.Single(rmas.Requests);

        await workflow.SetShippingAsync(rmaId.Value, null, TestContext.Current.CancellationToken);
        await workflow.ConfirmAsync(rmaId.Value, TestContext.Current.CancellationToken);

        Assert.Equal(3, commits.CommitCount);
    }

    /// <summary>
    /// Stands in for the EF unit of work. It models the production behaviour that matters here:
    /// a staged write is not durable until the workflow commits it.
    /// </summary>
    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        private readonly FakeRmaRepository _rmas;
        private readonly FakeDeviceRepository _devices;

        public RecordingUnitOfWork(FakeRmaRepository rmas, FakeDeviceRepository devices)
        {
            _rmas = rmas;
            _devices = devices;
        }

        public int CommitCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            CommitCount++;
            return Task.FromResult(_rmas.Requests.Count + _devices.Devices.Count);
        }
    }

    private static DeviceLookupResult Found(Device device) => new() { Device = device };

    /// <summary>Attaches the supplied device's product to matching lines. In the real host the
    /// EF include does this on read; the in-memory repository stores lines without their
    /// navigation, so a test wanting to price a repair states which product each line has.</summary>
    private static void WithProducts(RmaRequest request, params Device[] devices)
    {
        foreach (var line in request.Lines)
        {
            line.Device = devices.First(d => d.Id == line.DeviceId);
        }
    }

    private static Product Product(string sku, string name) => new() { Id = Guid.NewGuid(), Sku = sku, Name = name };

    private static TriageResult Triage(bool resolved, string summary = "The supply is not reaching the unit.") => new()
    {
        Verdict = new TriageVerdict
        {
            Resolved = resolved,
            Confidence = 0.8,
            Summary = summary,
            ProblemCategoryCode = "power.supply",
            Steps = [new TriageStep { Instruction = "Reseat the supply.", ArticleId = "kb-0001" }],
            ArticleIds = ["kb-0001"],
        },
        Sources = [new RetrievedArticle
        {
            Id = "kb-0001", Title = "Power", Category = "power", Score = 0.9,
        }],
    };

    // ---------- start ----------

    [Fact]
    public async Task An_empty_batch_is_refused()
    {
        var (workflow, rmas, _, _, _) = Build();

        var result = await workflow.StartAsync(
            new StartRmaCommand { Devices = [], Today = Today }, CancellationToken.None);

        Assert.Equal(StepOutcome.NotEligible, result.Outcome);
        Assert.Empty(rmas.Requests);
    }

    [Fact]
    public async Task A_covered_item_starts_a_draft_with_a_line()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);

        var result = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result.Outcome);
        Assert.Equal(RmaRequestStatus.Draft, result.Status);

        var request = Assert.Single(rmas.Requests);
        Assert.Equal(device.Id, Assert.Single(request.Lines).DeviceId);
        Assert.Equal(nameof(RmaDecisionReason.InWarrantyConfirmedDefect), request.EligibilityReason);
    }

    [Fact]
    public async Task An_unknown_item_is_refused_and_nothing_is_written()
    {
        var (workflow, rmas, _, _, _) = Build();

        var result = await workflow.StartAsync(
            new StartRmaCommand
            {
                Devices = [new DeviceLookupResult { MatchScore = 0d }],
                Today = Today,
            },
            CancellationToken.None);

        Assert.Equal(StepOutcome.NotEligible, result.Outcome);
        Assert.Empty(rmas.Requests);
    }

    [Fact]
    public async Task An_out_of_warranty_item_starts_a_paid_repair()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var device = TestData.Device(serial: "OLD-1", end: Today.AddYears(-1));
        devices.Devices.Add(device);

        var result = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        // Out of warranty used to be a refusal. It is now a paid repair: the return starts,
        // and the payment step collects the product's repair price before anything ships.
        Assert.Equal(StepOutcome.Continue, result.Outcome);
        Assert.NotNull(result.RmaId);

        var request = Assert.Single(rmas.Requests);
        Assert.Equal(RmaRequestKind.PaidRepair, request.Kind);
        Assert.Equal(device.Id, Assert.Single(request.Lines).DeviceId);
        Assert.Equal(nameof(RmaDecisionReason.OutOfWarranty), request.EligibilityReason);
        Assert.Empty(request.Exclusions);
    }

    [Fact]
    public async Task A_batch_needing_review_stops_before_anything_is_written()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var device = TestData.Device(serial: "BUSY-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);
        devices.OpenRequestByDevice[device.Id] = true;

        var result = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        // Routed to a person, and specifically not created: a duplicate draft would send the
        // customer into a second return for a unit already on its way back.
        Assert.Equal(StepOutcome.RequiresHumanReview, result.Outcome);
        Assert.Empty(rmas.Requests);
        Assert.Equal(RmaDecisionReason.DuplicateOpenRequest, Assert.Single(result.ExcludedItems).Reason);
    }

    // ---------- mixed batches ----------

    [Fact]
    public async Task A_mixed_batch_creates_a_paid_return_with_the_out_of_warranty_unit_included()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var covered = TestData.Device(serial: "GOOD-1", end: Today.AddMonths(3));
        var old = TestData.Device(serial: "OLD-1", end: Today.AddYears(-2));
        devices.Devices.AddRange([covered, old]);

        var result = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(covered), Found(old)], Today = Today },
            CancellationToken.None);

        // The covered unit rides free in the same return as a paid repair for the old one.
        // An uncovered accessory used to sink the whole unit; now it becomes a paid line.
        Assert.Equal(StepOutcome.Continue, result.Outcome);
        Assert.NotNull(result.RmaId);

        var request = Assert.Single(rmas.Requests);
        Assert.Equal(RmaRequestKind.PaidRepair, request.Kind);
        Assert.Equal(2, request.Lines.Count);
        Assert.Contains(request.Lines, l => l.DeviceId == covered.Id);
        Assert.Contains(request.Lines, l => l.DeviceId == old.Id);
        Assert.Empty(request.Exclusions);
        Assert.Equal(nameof(RmaDecisionReason.OutOfWarranty), request.EligibilityReason);
    }

    [Fact]
    public async Task A_partial_return_is_reported_distinctly_from_a_complete_one()
    {
        var (workflow, _, devices, _, _) = Build();
        var covered = TestData.Device(serial: "GOOD-1", end: Today.AddMonths(3));
        var outside = TestData.Device(serial: "WIN-1", end: Today.AddYears(-2), shipped: Now.AddDays(-800));
        devices.Devices.AddRange([covered, outside]);

        var partial = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(covered), Found(outside)], Today = Today },
            CancellationToken.None);

        Assert.NotEqual(StepOutcome.Continue, partial.Outcome);

        // The message has to stand on its own for a customer reading it in a browser.
        Assert.Contains("1 of 2", partial.Message);
        Assert.Contains("OutsideReturnWindow", partial.Message);
    }

    [Fact]
    public async Task A_fully_eligible_batch_reports_a_plain_continue()
    {
        var (workflow, rmas, _, _, _) = Build();
        var first = TestData.Device(serial: "A", end: Today.AddMonths(3));
        var second = TestData.Device(serial: "B", end: Today.AddMonths(3));

        var result = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(first), Found(second)], Today = Today },
            CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result.Outcome);
        Assert.Empty(result.ExcludedItems);
        Assert.Equal(2, rmas.Requests[0].Lines.Count);
    }

    [Fact]
    public async Task A_review_item_is_left_out_of_a_mixed_batch_and_reported()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var covered = TestData.Device(serial: "GOOD-1", end: Today.AddMonths(3));
        var busy = TestData.Device(serial: "BUSY-1", end: Today.AddMonths(3));
        devices.Devices.AddRange([covered, busy]);
        devices.OpenRequestByDevice[busy.Id] = true;

        var result = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(covered), Found(busy)], Today = Today },
            CancellationToken.None);

        // The unit already has a return in flight, so it must not be added to a second one,
        // but it must not disappear either.
        Assert.Equal(StepOutcome.PartiallyCreated, result.Outcome);
        Assert.Equal(covered.Id, Assert.Single(rmas.Requests[0].Lines).DeviceId);

        var excluded = Assert.Single(result.ExcludedItems);
        Assert.Equal(busy.Id, excluded.DeviceId);
        Assert.Equal(RmaDecisionReason.DuplicateOpenRequest, excluded.Reason);
    }

    [Fact]
    public async Task A_batch_with_nothing_eligible_is_still_refused()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var first = TestData.Device(serial: "WIN-1", end: Today.AddYears(-2), shipped: Now.AddDays(-800));
        var second = TestData.Device(serial: "WIN-2", end: Today.AddYears(-3), shipped: Now.AddDays(-900));
        devices.Devices.AddRange([first, second]);

        var result = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(first), Found(second)], Today = Today },
            CancellationToken.None);

        // Proceeding with the eligible lines must not become a way to create an empty return.
        Assert.Equal(StepOutcome.NotEligible, result.Outcome);
        Assert.Null(result.RmaId);
        Assert.Empty(rmas.Requests);
        Assert.Equal(2, result.ExcludedItems.Count);
    }

    [Fact]
    public async Task An_unidentified_item_is_left_out_of_a_mixed_batch()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var covered = TestData.Device(serial: "GOOD-1", end: Today.AddMonths(3));
        devices.Devices.Add(covered);

        var result = await workflow.StartAsync(
            new StartRmaCommand
            {
                Devices = [Found(covered), new DeviceLookupResult { MatchScore = 0d, MatchedOn = "serial" }],
                Today = Today,
            },
            CancellationToken.None);

        Assert.Equal(StepOutcome.PartiallyCreated, result.Outcome);
        Assert.Equal(covered.Id, Assert.Single(rmas.Requests[0].Lines).DeviceId);

        var excluded = Assert.Single(result.ExcludedItems);
        Assert.Equal(RmaDecisionReason.DeviceNotFound, excluded.Reason);
        Assert.Equal(Guid.Empty, excluded.DeviceId);
        Assert.Equal("serial", excluded.MatchedOn);
    }

    [Fact]
    public async Task The_trace_covers_the_included_items_not_the_excluded_ones()
    {
        var covered = TestData.Device(serial: "GOOD-1", end: Today.AddMonths(3));
        var flagged = TestData.Device(serial: "STOLEN-1", end: Today.AddMonths(3));

        var options = DefaultOptions();
        options.FraudIndicatorSerialPrefixes = ["STOLEN-"];

        var (wf, rmas, _, _, _) = Build(new RmaPolicyEvaluator(
            Microsoft.Extensions.Options.Options.Create(options)));

        await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(covered), Found(flagged)], Today = Today },
            CancellationToken.None);

        var request = rmas.Requests[0];

        // Filing the return under the fraud reason would misstate why the customer qualifies.
        Assert.Equal(nameof(RmaDecisionReason.InWarrantyConfirmedDefect), request.EligibilityReason);
        Assert.Contains("warranty.active", request.PolicyRuleTrace);
        Assert.DoesNotContain("fraud", request.PolicyRuleTrace);
    }

    [Fact]
    public async Task Every_item_in_a_batch_is_evaluated()
    {
        var eligibility = new StubEligibilityService(new RmaEligibilityDecision
        {
            Eligibility = RmaEligibility.Eligible,
            Reason = RmaDecisionReason.InWarrantyConfirmedDefect,
            Explanation = "forced",
        });
        var (wf, _, _, _, _) = Build(eligibility);

        await wf.StartAsync(
            new StartRmaCommand
            {
                Devices =
                [
                    Found(TestData.Device(serial: "A", end: Today.AddMonths(3))),
                    Found(TestData.Device(serial: "B", end: Today.AddMonths(3))),
                    Found(TestData.Device(serial: "C", end: Today.AddMonths(3))),
                ],
                Today = Today,
            },
            CancellationToken.None);

        // Short-circuiting on the first problem would hide the rest from support, and the
        // aggregate decision is meaningless if it only ever saw one item.
        Assert.Equal(3, eligibility.CallCount);
    }

    // ---------- triage cannot authorise anything ----------

    [Fact]
    public async Task A_confirmed_fault_annotates_the_request_without_changing_its_status()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);

        var start = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        var result = await workflow.ApplyTriageAsync(
            start.RmaId!.Value, Triage(resolved: false), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result.Outcome);
        Assert.Equal(RmaRequestStatus.Draft, result.Status);

        var request = rmas.Requests[0];
        var line = request.Lines.First();
        Assert.Equal("DefectConfirmed", line.TriageVerdict);
        Assert.Equal("power.supply", line.ProblemCategoryCode);
        Assert.Contains("kb-0001", line.TriageArticleIds);
    }

    [Fact]
    public async Task A_resolved_verdict_cancels_but_never_approves()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);

        var start = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        var result = await workflow.ApplyTriageAsync(
            start.RmaId!.Value, Triage(resolved: true), CancellationToken.None);

        // Cancelled, not completed and not approved. Support must still be able to audit it.
        Assert.Equal(StepOutcome.ResolvedByTroubleshooting, result.Outcome);
        Assert.Equal(RmaRequestStatus.Cancelled, rmas.Requests[0].Status);
        Assert.NotNull(rmas.Requests[0].CancellationReason);
    }

    [Fact]
    public async Task Triage_on_an_unknown_return_is_reported_rather_than_thrown()
    {
        var (workflow, _, _, _, _) = Build();

        var result = await workflow.ApplyTriageAsync(
            Guid.NewGuid(), Triage(resolved: true), CancellationToken.None);

        Assert.Equal(StepOutcome.NotEligible, result.Outcome);
    }

    [Fact]
    public async Task A_very_long_triage_summary_is_bounded_before_storage()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);

        var start = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        await workflow.ApplyTriageAsync(
            start.RmaId!.Value, Triage(resolved: false, new string('s', 10_000)), CancellationToken.None);

        Assert.True(rmas.Requests[0].TroubleshootingSummary!.Length <= 2000);
    }

    // ---------- problems ----------

    [Fact]
    public async Task A_problem_for_a_device_that_is_not_on_the_return_is_ignored()
    {
        var (workflow, rmas, devices, _, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);

        var start = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        await workflow.RecordProblemsAsync(
            start.RmaId!.Value,
            [new ProblemEntry { DeviceId = Guid.NewGuid(), Description = "not ours" }],
            CancellationToken.None);

        // Silently attaching a fault to the wrong line would corrupt the reason for the return.
        Assert.Equal(string.Empty, rmas.Requests[0].Lines.First().ProblemDescription);
    }

    // ---------- shipping and payment ----------

    [Fact]
    public async Task A_free_return_is_approved_without_taking_payment()
    {
        var (workflow, rmas, devices, payments, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);
        payments.PaymentRequired = false;

        var start = await workflow.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        var result = await workflow.SetShippingAsync(start.RmaId!.Value, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(RmaRequestStatus.Approved, result.Status);
        Assert.Equal(0m, rmas.Requests[0].ShippingCharge);
        Assert.Empty(payments.Authorizations);
    }

    [Fact]
    public async Task A_paid_return_waits_for_payment_before_approval()
    {
        // The real policy now produces the paid path by itself: an out-of-warranty unit is
        // a paid repair, so this needs no stub to reach the billing logic.
        var (wf, rmas, devices, payments, _) = Build();
        var device = TestData.Device(serial: "OLD-1", end: Today.AddYears(-2));
        device.Product = Product("AX-200", "AX-200 Terminal");
        devices.Devices.Add(device);
        payments.PaymentRequired = true;

        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);
        WithProducts(rmas.Requests[0], device);

        var result = await wf.SetShippingAsync(start.RmaId!.Value, Guid.NewGuid(), CancellationToken.None);

        // The status must be pending payment, never approved: nothing ships until money moves.
        Assert.Equal(RmaRequestStatus.PendingPayment, result.Status);
        Assert.Equal(StepOutcome.AwaitingPayment, result.Outcome);
        Assert.Equal(149m, rmas.Requests[0].DepositAmount);
    }

    [Fact]
    public async Task A_covered_tier_asks_the_customer_for_nothing()
    {
        var (wf, rmas, _, payments, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        rmas.Requests[0].WarrantyTier = "standard";
        payments.PaymentRequired = false;

        await wf.SetShippingAsync(start.RmaId!.Value, Guid.NewGuid(), CancellationToken.None);

        // A covered plan is a free replacement, so nothing is charged and no payment check
        // happens with a price on it.
        Assert.Equal(RmaRequestKind.Warranty, rmas.Requests[0].Kind);
        Assert.Equal(0m, rmas.Requests[0].ShippingCharge);
        Assert.Equal(0m, rmas.Requests[0].DepositAmount);
        Assert.Equal(0m, payments.RequiredChecks[0].Total.Amount);
    }

    [Fact]
    public async Task An_out_of_warranty_item_charges_the_products_repair_price()
    {
        var (wf, rmas, devices, payments, _) = Build();
        var device = TestData.Device(serial: "OLD-1", end: Today.AddYears(-2));
        device.Product = Product("AX-400", "AX-400 Terminal");
        devices.Devices.Add(device);
        payments.PaymentRequired = true;

        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);
        WithProducts(rmas.Requests[0], device);

        await wf.SetShippingAsync(start.RmaId!.Value, Guid.NewGuid(), CancellationToken.None);

        // The charge is the product's price from the repair price table, not a tier fee.
        Assert.Equal(69m, rmas.Requests[0].DepositAmount);
        Assert.Equal(0m, rmas.Requests[0].ShippingCharge);
        Assert.Equal(69m, payments.RequiredChecks[0].Total.Amount);
    }

    [Fact]
    public async Task A_paid_repair_sums_the_repair_prices_of_its_lines()
    {
        var (wf, rmas, devices, payments, _) = Build();
        var first = TestData.Device(serial: "OLD-1", end: Today.AddYears(-2));
        first.Product = Product("AX-200", "AX-200 Terminal");
        var second = TestData.Device(serial: "OLD-2", end: Today.AddYears(-3));
        second.Product = Product("AX-400", "AX-400 Terminal");
        devices.Devices.AddRange([first, second]);
        payments.PaymentRequired = true;

        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(first), Found(second)], Today = Today },
            CancellationToken.None);
        WithProducts(rmas.Requests[0], first, second);

        await wf.SetShippingAsync(start.RmaId!.Value, Guid.NewGuid(), CancellationToken.None);

        var request = rmas.Requests[0];
        Assert.Equal(RmaRequestKind.PaidRepair, request.Kind);
        Assert.Equal(2, request.Lines.Count);

        // Each unit brings its own product price, charged once per request.
        Assert.Equal(218m, request.DepositAmount);
        Assert.Equal(218m, payments.RequiredChecks[0].Total.Amount);
    }

    [Fact]
    public async Task Authorising_a_zero_amount_request_needs_no_gateway()
    {
        var (wf, rmas, _, payments, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        var result = await wf.AuthorizePaymentAsync(start.RmaId!.Value, "tok", CancellationToken.None);

        Assert.Equal(RmaRequestStatus.Approved, result.Status);
        Assert.Empty(payments.Authorizations);
        Assert.NotNull(rmas.Requests[0]);
    }

    [Fact]
    public async Task A_declined_payment_routes_to_a_person_and_takes_no_money()
    {
        var (wf, rmas, _, payments, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        rmas.Requests[0].DepositAmount = 89m;
        payments.AuthorizeResult = Result<PaymentReceipt>.Failure(
            "card_declined", "card declined");

        var result = await wf.AuthorizePaymentAsync(start.RmaId!.Value, "tok", CancellationToken.None);

        // Escalate rather than approve: a failed charge must not become a free replacement.
        Assert.Equal(StepOutcome.RequiresHumanReview, result.Outcome);
        Assert.Null(rmas.Requests[0].PaymentTransactionId);
        Assert.NotEqual(RmaRequestStatus.Approved, rmas.Requests[0].Status);
    }

    [Fact]
    public async Task A_deposit_is_authorised_for_capture_later()
    {
        var (wf, rmas, _, payments, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        rmas.Requests[0].DepositAmount = 89m;
        payments.AuthorizeResult = Result<PaymentReceipt>.Success(new PaymentReceipt
        {
            TransactionId = "txn_1",
            Purpose = PaymentPurpose.Deposit,
            Amount = new Money(89m, "USD"),
            Status = PaymentStatus.Authorized,
        });

        var result = await wf.AuthorizePaymentAsync(start.RmaId!.Value, "tok", CancellationToken.None);

        Assert.Equal(RmaRequestStatus.Approved, result.Status);
        Assert.Equal("txn_1", rmas.Requests[0].PaymentTransactionId);

        var authorization = Assert.Single(payments.Authorizations);
        Assert.Equal(PaymentPurpose.Deposit, authorization.Purpose);
        Assert.Equal(PaymentCaptureMode.ManualLater, authorization.CaptureMode);
        Assert.Equal(89m, authorization.Amount.Amount);
    }

    [Fact]
    public async Task Shipping_alone_is_captured_immediately()
    {
        var (wf, rmas, _, payments, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        rmas.Requests[0].ShippingCharge = 12m;
        rmas.Requests[0].DepositAmount = 0m;
        payments.AuthorizeResult = Result<PaymentReceipt>.Success(new PaymentReceipt
        {
            TransactionId = "txn_2",
            Purpose = PaymentPurpose.Shipping,
            Amount = new Money(12m, "USD"),
            Status = PaymentStatus.Captured,
        });

        await wf.AuthorizePaymentAsync(start.RmaId!.Value, "tok", CancellationToken.None);

        var authorization = Assert.Single(payments.Authorizations);
        Assert.Equal(PaymentPurpose.Shipping, authorization.Purpose);
        Assert.Equal(PaymentCaptureMode.Immediate, authorization.CaptureMode);
    }

    [Fact]
    public async Task The_payment_reference_identifies_the_return_and_its_units()
    {
        var (wf, rmas, _, payments, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        rmas.Requests[0].DepositAmount = 10m;
        payments.AuthorizeResult = Result<PaymentReceipt>.Success(new PaymentReceipt
        {
            TransactionId = "txn_3",
            Purpose = PaymentPurpose.Deposit,
            Amount = new Money(10m, "USD"),
            Status = PaymentStatus.Authorized,
        });

        await wf.AuthorizePaymentAsync(start.RmaId!.Value, "tok", CancellationToken.None);

        // Reconciliation depends on this: the number to quote a customer, and the unit.
        var authorization = Assert.Single(payments.Authorizations);
        Assert.Equal(rmas.Requests[0].RmaNumber, authorization.ReferenceNumbers[0]);
        Assert.Contains(device.Id.ToString(), authorization.ReferenceNumbers);
    }

    // ---------- confirmation ----------

    [Fact]
    public async Task A_draft_cannot_be_confirmed()
    {
        var (wf, _, devices, _, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);

        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        var result = await wf.ConfirmAsync(start.RmaId!.Value, CancellationToken.None);

        Assert.Equal(StepOutcome.RequiresHumanReview, result.Outcome);
        Assert.Equal(RmaRequestStatus.Draft, result.Status);
    }

    [Fact]
    public async Task An_approved_return_confirms_to_awaiting_shipment()
    {
        var (wf, rmas, _, payments, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);
        payments.PaymentRequired = false;

        await wf.SetShippingAsync(start.RmaId!.Value, Guid.NewGuid(), CancellationToken.None);
        var result = await wf.ConfirmAsync(start.RmaId!.Value, CancellationToken.None);

        Assert.Equal(StepOutcome.Completed, result.Outcome);
        Assert.Equal(RmaRequestStatus.AwaitingShipment, rmas.Requests[0].Status);
    }

    [Fact]
    public async Task A_cancelled_return_cannot_be_confirmed()
    {
        var (wf, rmas, devices, _, _) = Build();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        var start = await wf.StartAsync(
            new StartRmaCommand { Devices = [Found(device)], Today = Today }, CancellationToken.None);

        await wf.ApplyTriageAsync(start.RmaId!.Value, Triage(resolved: true), CancellationToken.None);
        var result = await wf.ConfirmAsync(start.RmaId!.Value, CancellationToken.None);

        // The AI said "fixed". That must never become a completed return.
        Assert.Equal(StepOutcome.RequiresHumanReview, result.Outcome);
        Assert.Equal(RmaRequestStatus.Cancelled, rmas.Requests[0].Status);
    }

    [Fact]
    public async Task Confirming_an_unknown_return_is_reported_rather_than_thrown()
    {
        var (wf, _, _, _, _) = Build();

        Assert.Equal(
            StepOutcome.NotEligible,
            (await wf.ConfirmAsync(Guid.NewGuid(), CancellationToken.None)).Outcome);
    }
}

/// <summary>Money is a value type that is easy to misuse, and a mis-summed amount here becomes
/// a mischarged customer.</summary>
public sealed class MoneyTests
{
    [Fact]
    public void Adding_the_same_currency_is_allowed()
    {
        var total = new Money(10m, "USD").Plus(new Money(5.5m, "USD"));
        Assert.Equal(15.5m, total.Amount);
    }

    [Theory]
    [InlineData("USD", "EUR")]
    [InlineData("USD", "GBP")]
    public void Mixing_currencies_is_refused_rather_than_silently_wrong(string left, string right)
    {
        // Converting without a rate would invent a number. Refusing is the only honest answer.
        Assert.Throws<InvalidOperationException>(
            () => new Money(10m, left).Plus(new Money(5m, right)));
    }

    [Fact]
    public void Currency_comparison_ignores_case() =>
        Assert.Equal(
            3m,
            new Money(1m, "usd").Plus(new Money(2m, "USD")).Amount);

    [Fact]
    public void Zero_normalises_the_currency() =>
        Assert.Equal("USD", Money.Zero("usd").CurrencyCode);

    [Fact]
    public void A_blank_currency_is_refused() =>
        Assert.Throws<ArgumentException>(() => Money.Zero("  "));

    [Fact]
    public void Sign_predicates_agree_with_the_amount()
    {
        Assert.True(new Money(0.01m, "USD").IsPositive);
        Assert.True(Money.Zero("USD").IsZero);
        Assert.True(new Money(-1m, "USD").IsNegative);
    }

    [Fact]
    public void Formatting_is_culture_invariant_and_two_decimal()
    {
        // The web layer renders this to customers; a comma decimal separator would be wrong
        // outside the US regardless of the server's locale.
        Assert.Equal("1234.50 USD", new Money(1234.5m, "USD").ToString());
    }

    [Fact]
    public void Multiplying_keeps_the_currency() =>
        Assert.Equal("30.00 GBP", new Money(10m, "GBP").Times(3).ToString());
}
