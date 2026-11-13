using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Domain.Triage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Domain.Rma;

/// <summary>
/// Orchestrates the RMA lifecycle. Every transition goes through
/// <see cref="IEligibilityService"/>, so the AI can never move a request into an
/// approvable state on its own.
/// </summary>
public interface IRmaWorkflow
{
    Task<StepResult> StartAsync(StartRmaCommand command, CancellationToken cancellationToken);

    Task<StepResult> ApplyTriageAsync(
        Guid rmaId,
        TriageResult triage,
        string? customerStatement,
        CancellationToken cancellationToken);

    Task<StepResult> RecordProblemsAsync(Guid rmaId, IReadOnlyList<ProblemEntry> problems, CancellationToken cancellationToken);

    Task<StepResult> SetShippingAsync(Guid rmaId, Guid? addressId, CancellationToken cancellationToken);

    Task<StepResult> AuthorizePaymentAsync(Guid rmaId, string? paymentMethodToken, CancellationToken cancellationToken);

    Task<StepResult> ConfirmAsync(Guid rmaId, CancellationToken cancellationToken);
}

public sealed record StartRmaCommand
{
    public required IReadOnlyList<DeviceLookupResult> Devices { get; init; }

    public required DateOnly Today { get; init; }

    /// <summary>
    /// The customer this return belongs to. Optional because an unidentified walk-in return is
    /// legitimate, but every customer-facing step after this one (choosing an address, quoting a
    /// fee, emailing the reference) needs it.
    /// </summary>
    public Guid? CustomerId { get; init; }

    public string? RegionCode { get; init; }

    /// <summary>
    /// What the customer says is wrong, when the caller already knows. Optional, because the
    /// wizard only collects it at the triage step; supplied here it lets the excluded-cause
    /// rules (liquid, impact, cosmetic) refuse the item at intake instead of much later.
    /// </summary>
    public string? ProblemDescription { get; init; }

    public string CurrencyCode { get; init; } = "USD";
}

public sealed record ProblemEntry
{
    public required Guid DeviceId { get; init; }

    public required string Description { get; init; }

    public string? CategoryCode { get; init; }

    public string? WhatCustomerTried { get; init; }
}

public enum StepOutcome
{
    Continue = 0,
    ResolvedByTroubleshooting = 1,
    NotEligible = 2,
    RequiresHumanReview = 3,
    AwaitingPayment = 4,
    Completed = 5,

    /// <summary>
    /// A return was created, but not for every item that was supplied. What was left out, and
    /// why, is in <see cref="StepResult.ExcludedItems"/>.
    ///
    /// Deliberately distinct from <see cref="Continue"/>: a caller that ignores the detail list
    /// still cannot mistake a partial return for a complete one.
    /// </summary>
    PartiallyCreated = 6,

    /// <summary>
    /// The request is not in a state where this step is legal. Deliberately distinct from
    /// <see cref="RequiresHumanReview"/>: nothing needs a person here, the caller posted a step
    /// out of order (a skipped or replayed wizard page).
    /// </summary>
    InvalidTransition = 7,
}

/// <summary>
/// An item that was not included in the return, with the policy reason it was left out.
/// Reported rather than dropped, because a customer who handed over three things and had one
/// quietly discarded would have no way of knowing to ask about it.
/// </summary>
public sealed record ExcludedItem
{
    /// <summary>Guid.Empty when the identifier did not resolve to a catalog item.</summary>
    public Guid DeviceId { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>Which field the caller looked the item up by, e.g. "serial".</summary>
    public string? MatchedOn { get; init; }

    public required RmaEligibility Eligibility { get; init; }

    public required RmaDecisionReason Reason { get; init; }

    public string? Explanation { get; init; }
}

public sealed record StepResult
{
    public required StepOutcome Outcome { get; init; }

    public Guid? RmaId { get; init; }

    public string? RmaNumber { get; init; }

    public required string Message { get; init; }

    public RmaEligibilityDecision? Decision { get; init; }

    public RmaRequestStatus Status { get; set; } = RmaRequestStatus.Draft;

    /// <summary>
    /// Items that were not included in the return. Always populated when
    /// <see cref="Outcome"/> is <see cref="StepOutcome.PartiallyCreated"/>.
    /// </summary>
    public IReadOnlyList<ExcludedItem> ExcludedItems { get; init; } = [];
}

public sealed partial class RmaWorkflow(
    IRmaRepository rmaRepository,
    IDeviceRepository deviceRepository,
    IEligibilityService eligibilityService,
    IPaymentGateway paymentGateway,
    IUnitOfWork unitOfWork,
    IOptions<RmaPolicyOptions> policyOptions,
    IClock clock,
    ILogger<RmaWorkflow> logger) : IRmaWorkflow
{
    private readonly RmaPolicyOptions _policy = policyOptions.Value;

    public async Task<StepResult> StartAsync(
        StartRmaCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Devices.Count == 0)
        {
            return new StepResult
            {
                Outcome = StepOutcome.NotEligible,
                Message = "No items were supplied.",
                Decision = Denied(RmaDecisionReason.DeviceNotFound, "No items were supplied."),
            };
        }

        // Evaluate every device. A single ineligible device does not sink the batch, because
        // customers routinely return a mix: a working accessory alongside a broken unit.
        var decisions = new List<(DeviceLookupResult Device, RmaEligibilityDecision Decision)>();

        foreach (var device in command.Devices)
        {
            if (device.Device is null)
            {
                decisions.Add((device, Denied(
                    RmaDecisionReason.DeviceNotFound,
                    "That item is not in our product catalog.")));
                continue;
            }

            var hasOpen = await deviceRepository.HasOpenRequestAsync(
                device.Device.Id,
                cancellationToken);

            var decision = eligibilityService.Evaluate(new RmaPolicyRequest
            {
                Device = device.Device,
                Today = command.Today,
                RegionCode = command.RegionCode,
                ProblemDescription = command.ProblemDescription,
                HasOpenRequestForDevice = hasOpen,
            });

            decisions.Add((device, decision));
        }

        // Every device that clears policy becomes a line. An out-of-warranty unit is still a
        // line: it just becomes a paid repair instead of a free one. Only decisions that do
        // not allow creation (unknown device, excluded cause, needs review, ...) are left out.
        var includable = decisions.Where(d => d.Decision.AllowsRmaCreation).ToList();
        var free = includable.Where(d => d.Decision.Eligibility == RmaEligibility.Eligible).ToList();
        var paid = includable.Where(d => d.Decision.Eligibility == RmaEligibility.EligibleForPaidRepair).ToList();
        var excluded = decisions.Where(d => !d.Decision.AllowsRmaCreation).ToList();

        // Only refuse the whole batch when nothing in it can proceed. Refusing a batch because
        // one accessory is out of warranty would throw away a return the customer is entitled
        // to (a paid repair is still a return), and the original comment claimed this was
        // already the behaviour.
        if (includable.Count == 0)
        {
            var worst = Aggregate(decisions.Select(d => d.Decision));

            logger.LogInformation(
                "RMA start blocked by policy: {Reason} (rules: {Rules})",
                worst.Reason,
                string.Join(", ", worst.MatchedRuleIds));

            return new StepResult
            {
                Outcome = worst.Eligibility switch
                {
                    RmaEligibility.NotEligible => StepOutcome.NotEligible,
                    RmaEligibility.RequiresHumanReview => StepOutcome.RequiresHumanReview,
                    _ => StepOutcome.RequiresHumanReview,
                },
                Message = worst.Explanation,
                Decision = worst,

                // Nothing was created, so every item the customer offered is an excluded item.
                // Reporting them keeps the wizard from silently losing half the request.
                ExcludedItems = excluded.Select(ToExcludedItem).ToList(),
            };
        }

        // The trace covers the items actually in the return. Using the batch's worst decision
        // would file a return under the reason one excluded item failed, which misstates why
        // the customer is eligible at all. A paid line governs a paid return: if anything in
        // the batch is not covered, the request is a PaidRepair and must be filed under the
        // out-of-warranty reason rather than under a covered line's reason.
        var governing = (paid.Count > 0 ? paid : free)
            .OrderByDescending(d => d.Decision.MatchedRuleIds.Count)
            .First();
        var governingDecision = governing.Decision;

        var now = clock.UtcNow;
        var rmaNumber = await rmaRepository.NextRmaNumberAsync(command.Today, cancellationToken);

        var request = new RmaRequest
        {
            Id = Guid.NewGuid(),
            RmaNumber = rmaNumber,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Status = RmaRequestStatus.Draft,
            Kind = paid.Count > 0 ? RmaRequestKind.PaidRepair : RmaRequestKind.Warranty,
            CustomerId = command.CustomerId,
            CurrencyCode = command.CurrencyCode,

            // The tier the request is priced against. This used to be left null, and because
            // SetShippingAsync resolves the tier by name, every return was quoted at zero
            // regardless of policy. A multi-item return spanning two tiers is genuinely
            // ambiguous: the schema charges once per request, not per line, so the governing
            // device sets the tier and the trace records which one.
            WarrantyTier = governing.Device.GoverningWarranty?.Tier
                ?? _policy.DefaultTier,

            EligibilityReason = governingDecision.Reason.ToString(),
            PolicyRuleTrace = string.Join(
                ", ",
                includable.SelectMany(d => d.Decision.MatchedRuleIds).Distinct(StringComparer.OrdinalIgnoreCase)),
        };

        foreach (var (device, _) in includable)
        {
            request.Lines.Add(new RmaLine
            {
                Id = Guid.NewGuid(),
                RmaRequestId = request.Id,
                DeviceId = device.Device!.Id,
                ProblemDescription = string.Empty,
            });
        }

        // Priced the moment the request exists, not when the customer reaches the shipping
        // step. The payment step authorises whatever is stored on the request, so a paid repair
        // that could be read before it was priced authorised zero and shipped for free.
        ApplyPricing(request, includable.Select(d => d.Device.Device!.Product));

        // Exclusions are persisted with the request, not returned only in the response. The
        // wizard warns on the page that follows this step, and a refresh or a new session must
        // not quietly make the warning disappear.
        var excludedItems = excluded.Select(ToExcludedItem).ToList();

        foreach (var (item, index) in excludedItems.Select((item, index) => (item, index)))
        {
            request.Exclusions.Add(new RmaExclusion
            {
                Id = Guid.NewGuid(),
                RmaRequestId = request.Id,
                DeviceId = item.DeviceId == Guid.Empty ? null : item.DeviceId,
                SerialNumber = item.SerialNumber,
                MatchedOn = item.MatchedOn,
                Eligibility = item.Eligibility.ToString(),
                Reason = item.Reason.ToString(),
                Explanation = item.Explanation,
                SortOrder = index,
            });
        }

        await rmaRepository.AddAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);

        if (excluded.Count == 0)
        {
            return new StepResult
            {
                Outcome = StepOutcome.Continue,
                RmaId = request.Id,
                RmaNumber = request.RmaNumber,
                Message = "Return started.",
                Decision = governingDecision,
                Status = request.Status,
            };
        }

        logger.LogInformation(
            "RMA {RmaNumber} created for {Included} of {Supplied} item(s); {Excluded} left out: {Reasons}.",
            request.RmaNumber,
            includable.Count,
            command.Devices.Count,
            excludedItems.Count,
            string.Join(", ", excludedItems.Select(i => i.Reason)));

        return new StepResult
        {
            Outcome = StepOutcome.PartiallyCreated,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Message =
                $"Return started for {includable.Count} of {command.Devices.Count} item(s). "
                + $"{excludedItems.Count} item(s) could not be included and need to be looked at: "
                + string.Join(
                    " ",
                    excludedItems.Select(i =>
                        $"{i.SerialNumber ?? i.MatchedOn ?? "One item"} ({i.Reason}): {i.Explanation}")),
            Decision = governingDecision,
            Status = request.Status,
            ExcludedItems = excludedItems,
        };
    }

    private static ExcludedItem ToExcludedItem(
        (DeviceLookupResult Device, RmaEligibilityDecision Decision) entry) => new()
    {
        DeviceId = entry.Device.Device?.Id ?? Guid.Empty,
        SerialNumber = entry.Device.Device?.SerialNumber,
        MatchedOn = entry.Device.MatchedOn,
        Eligibility = entry.Decision.Eligibility,
        Reason = entry.Decision.Reason,
        Explanation = entry.Decision.Explanation,
    };

    public async Task<StepResult> ApplyTriageAsync(
        Guid rmaId,
        TriageResult triage,
        string? customerStatement,
        CancellationToken cancellationToken)
    {
        var request = await LoadAsync(rmaId, cancellationToken);

        if (request is null)
        {
            return NotFound();
        }

        // Triage is the first step after intake. Allowing it from any other status let a
        // replayed POST "resolve" a return that had already been confirmed, cancelling it.
        if (RefuseUnless(request, [RmaRequestStatus.Draft], "troubleshooting") is { } cannotTriage)
        {
            return cannotTriage;
        }

        // AI influence is limited to annotation and to the "resolved" early exit. Note that
        // even a resolved verdict does not close the RMA: it records the outcome and leaves
        // the request cancelled, which is a state support can still audit.
        request.TroubleshootingSummary = Truncate(triage.Verdict.Summary, 2000);

        var articleIds = string.Join(',', triage.Sources.Select(s => s.Id));
        var stepsJson = System.Text.Json.JsonSerializer.Serialize(triage.Verdict.Steps);

        foreach (var line in request.Lines)
        {
            line.TriageVerdict = triage.Verdict.Resolved ? "ResolvedByTroubleshooting" : "DefectConfirmed";
            line.TriageConfidence = triage.Verdict.Confidence;
            line.TriageArticleIds = Truncate(articleIds, 1000);
            line.RecommendedStepsJson = Truncate(stepsJson, 4000);
            line.ProblemCategoryCode ??= triage.Verdict.ProblemCategoryCode;
        }

        request.UpdatedAtUtc = clock.UtcNow;

        if (triage.Verdict.Resolved)
        {
            request.Status = RmaRequestStatus.Cancelled;
            request.CancellationReason = "Resolved during automated troubleshooting.";

            await rmaRepository.UpdateAsync(request, cancellationToken);
            await CommitAsync(cancellationToken);

            return new StepResult
            {
                Outcome = StepOutcome.ResolvedByTroubleshooting,
                RmaId = request.Id,
                RmaNumber = request.RmaNumber,
                Message = "Troubleshooting resolved the issue, so no return is needed.",
                Status = request.Status,
            };
        }

        // The customer's description of the fault only exists from this step onward, which is
        // exactly why the excluded-cause rules could never fire: intake evaluates a device, not
        // a statement. The same deterministic policy runs again here, with that statement as
        // its input, before the request is allowed to continue.
        var decision = ReEvaluate(request, _ => customerStatement);

        if (decision is not null && !decision.AllowsRmaCreation)
        {
            return await RefuseAsync(request, decision, cancellationToken);
        }

        // A cause the deployment prices as a paid repair (cosmetic wear, say) changes what the
        // customer owes, so the request becomes a paid repair and is priced here, before the
        // shipping and payment steps can ask for money against a stale zero.
        if (decision is { Eligibility: RmaEligibility.EligibleForPaidRepair }
            && request.Kind != RmaRequestKind.PaidRepair)
        {
            request.Kind = RmaRequestKind.PaidRepair;
            request.EligibilityReason = Truncate(decision.Reason.ToString(), 64);
            ApplyPricing(request, request.Lines.Select(l => l.Device?.Product));
        }

        await rmaRepository.UpdateAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);

        return new StepResult
        {
            Outcome = StepOutcome.Continue,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Message = "Troubleshooting confirmed the fault. Please continue with the return.",
            Status = request.Status,
        };
    }

    /// <summary>
    /// Runs the deterministic policy again, now that a fault description exists.
    /// </summary>
    /// <remarks>
    /// Three inputs are deliberately not resupplied. The region is not re-applied because a
    /// unit that needed review for its region never became a line at intake, so omitting it
    /// cannot relax anything. The duplicate check is not re-asked because this request's own
    /// line is open, and the repository would report every in-flight return as a duplicate of
    /// itself. AI confidence is never passed, because the AI is advisory and may not move
    /// eligibility in either direction.
    ///
    /// The description is chosen per line because the two callers have different numbers of
    /// them: triage has one statement for the whole request, and the problems step has one per
    /// line. Either way one refused line stops the whole request — keeping it would hand out a
    /// repair the rules do not allow.
    /// </remarks>
    private RmaEligibilityDecision? ReEvaluate(RmaRequest request, Func<RmaLine, string?> statementOf)
    {
        var decisions = request.Lines
            .Where(l => l.Device is not null)
            .Select(l => eligibilityService.Evaluate(new RmaPolicyRequest
            {
                Device = l.Device!,
                Today = clock.Today,
                ProblemDescription = statementOf(l),
                HasOpenRequestForDevice = false,
            }))
            .ToList();

        if (decisions.Count == 0)
        {
            // Nothing to evaluate against. Inventing a decision here would be worse than
            // leaving intake's on the record.
            return null;
        }

        return Aggregate(decisions);
    }

    /// <summary>
    /// Stops a return that the policy refused after the customer described the fault, and shapes
    /// the decision into the step result the wizard renders.
    /// </summary>
    /// <remarks>
    /// Rejected when the policy said no, NeedsReview when a person has to decide; both are
    /// terminal for the wizard, so the guarded steps cannot be driven afterwards. Intake's trace
    /// is kept and the rules that fired here are appended, so the audit trail shows both
    /// decisions rather than replacing one with the other.
    /// </remarks>
    private async Task<StepResult> RefuseAsync(
        RmaRequest request,
        RmaEligibilityDecision decision,
        CancellationToken cancellationToken)
    {
        request.Status = decision.Eligibility == RmaEligibility.NotEligible
            ? RmaRequestStatus.Rejected
            : RmaRequestStatus.NeedsReview;

        request.EligibilityReason = Truncate(decision.Reason.ToString(), 64);

        request.PolicyRuleTrace = Truncate(
            string.Join(
                ", ",
                (request.PolicyRuleTrace ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Concat(decision.MatchedRuleIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
            2000);

        request.UpdatedAtUtc = clock.UtcNow;

        await rmaRepository.UpdateAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);

        return new StepResult
        {
            Outcome = decision.Eligibility == RmaEligibility.NotEligible
                ? StepOutcome.NotEligible
                : StepOutcome.RequiresHumanReview,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Message = decision.Explanation,
            Decision = decision,
            Status = request.Status,
        };
    }

    public async Task<StepResult> RecordProblemsAsync(
        Guid rmaId,
        IReadOnlyList<ProblemEntry> problems,
        CancellationToken cancellationToken)
    {
        var request = await LoadAsync(rmaId, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        // The problems step is the only writer of a line's description, so it must not run on a
        // return that has already left Draft. Without this, a confirmed or cancelled return
        // still had its stored descriptions replaced by whatever a stale or hand-crafted page
        // posted, and the record of what was actually wrong with the item was lost after the
        // decision that depended on it.
        //
        // PendingPayment is deliberately excluded, although SetShippingAsync allows it so a
        // customer can go back and change the address: shipping re-prices on the way through,
        // while this step only re-evaluates the descriptions. An edit from the payment step would
        // therefore leave a quoted amount that no longer matches what the record says is wrong,
        // minutes before the customer authorises that amount. Changing the fault after a fee has
        // been quoted is a new return, not an edit to this one.
        if (RefuseUnless(request, [RmaRequestStatus.Draft], "problem") is { } cannotRecordProblems)
        {
            return cannotRecordProblems;
        }

        foreach (var entry in problems)
        {
            var line = request.Lines.FirstOrDefault(l => l.DeviceId == entry.DeviceId);
            if (line is null)
            {
                continue;
            }

            line.ProblemDescription = entry.Description.Trim();
            line.WhatCustomerTried = entry.WhatCustomerTried?.Trim();
            line.ProblemCategoryCode = entry.CategoryCode;
        }

        // The same excluded-cause rules, now against what the customer wrote here. Leaving this
        // to triage alone let a customer dodge them by describing a screen fault in the triage
        // statement and the spilled drink only in the per-line description below it.
        var decision = ReEvaluate(request, line => line.ProblemDescription);

        if (decision is not null && !decision.AllowsRmaCreation)
        {
            return await RefuseAsync(request, decision, cancellationToken);
        }

        // The other half of what triage does with the description: a cause this deployment prices
        // as a paid repair changes what the customer owes, so the request becomes a paid repair
        // and is priced before shipping and payment can ask for money against a free repair.
        // Without this, the same fault typed one step later than triage was ignored, and a
        // covered fault described at triage followed by cosmetic wear here went through free.
        if (decision is { Eligibility: RmaEligibility.EligibleForPaidRepair }
            && request.Kind != RmaRequestKind.PaidRepair)
        {
            request.Kind = RmaRequestKind.PaidRepair;
            request.EligibilityReason = Truncate(decision.Reason.ToString(), 64);
            ApplyPricing(request, request.Lines.Select(l => l.Device?.Product));
        }

        request.UpdatedAtUtc = clock.UtcNow;
        await rmaRepository.UpdateAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);

        return new StepResult
        {
            Outcome = StepOutcome.Continue,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Message = "Problem details recorded.",
            Status = request.Status,
        };
    }

    public async Task<StepResult> SetShippingAsync(
        Guid rmaId,
        Guid? addressId,
        CancellationToken cancellationToken)
    {
        var request = await LoadAsync(rmaId, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        // Reached from the problems step, and from the payment step when the customer goes
        // back to change the address. Any later status means shipping has already been decided,
        // so re-running it would re-price and re-approve a request that is already committed.
        if (RefuseUnless(request, [RmaRequestStatus.Draft, RmaRequestStatus.PendingPayment], "shipping") is { } cannotShip)
        {
            return cannotShip;
        }

        if (addressId is { } address)
        {
            request.ShipToAddressId = address;
        }

        // A paid repair charges the sum of its lines' product prices, once per request for all
        // the units being repaired. Stored as the deposit so the payment step authorises for
        // capture later — the customer is charged for the repair, not for shipping, and the
        // payment page's "authorised now, captured once the item is received" copy stays true.
        //
        // The covered amount is taken from the product price table, not from any tier: two
        // different products on the same request each bring their own price, and the charge
        // an out-of-warranty repair actually carries is the one the fee-quoting tool reports.
        ApplyPricing(request, request.Lines.Select(l => l.Device?.Product));

        var total = new Domain.Common.Money(request.ShippingCharge + request.DepositAmount,
            request.CurrencyCode);

        var paymentNeeded = await paymentGateway.IsPaymentRequiredAsync(
            new PaymentRequestContext { Total = total },
            cancellationToken);

        request.Status = paymentNeeded
            ? RmaRequestStatus.PendingPayment
            : RmaRequestStatus.Approved;

        request.UpdatedAtUtc = clock.UtcNow;
        await rmaRepository.UpdateAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);

        return new StepResult
        {
            Outcome = paymentNeeded ? StepOutcome.AwaitingPayment : StepOutcome.Continue,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Message = paymentNeeded
                ? "Payment is required before we can ship the replacement."
                : "No payment is due for this return.",
            Status = request.Status,
        };
    }

    public async Task<StepResult> AuthorizePaymentAsync(
        Guid rmaId,
        string? paymentMethodToken,
        CancellationToken cancellationToken)
    {
        var request = await LoadAsync(rmaId, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        // Payment is only legal once the shipping step has parked the request at
        // PendingPayment. Without this guard a draft posted straight to the payment endpoint
        // authorised its (zero) total, became Approved, and confirmed without triage, shipping,
        // or a charge — a free paid repair.
        if (RefuseUnless(request, [RmaRequestStatus.PendingPayment], "payment") is { } cannotPay)
        {
            return cannotPay;
        }

        var amount = new Domain.Common.Money(
            request.ShippingCharge + request.DepositAmount,
            request.CurrencyCode);

        // A zero total is already settled, and asking a real gateway to authorise nothing is
        // how it starts refusing requests. Skipping the call leaves one approval path below
        // instead of two copies of the same status write, update, and commit.
        if (!amount.IsZero)
        {
            var authorization = await paymentGateway.AuthorizeAsync(
                new PaymentAuthorizationRequest
                {
                    RmaNumber = request.RmaNumber,
                    Amount = amount,
                    Purpose = request.DepositAmount > 0 ? PaymentPurpose.Deposit : PaymentPurpose.Shipping,
                    CaptureMode = request.DepositAmount > 0
                        ? PaymentCaptureMode.ManualLater
                        : PaymentCaptureMode.Immediate,
                    CustomerEmail = request.Customer?.Email ?? "unknown",
                    PaymentMethodToken = paymentMethodToken,
                    ReferenceNumbers = [request.RmaNumber, .. request.Lines.Select(l => l.DeviceId.ToString())],
                },
                cancellationToken);

            if (authorization.IsFailure)
            {
                return new StepResult
                {
                    Outcome = StepOutcome.RequiresHumanReview,
                    RmaId = request.Id,
                    RmaNumber = request.RmaNumber,
                    Message = authorization.ErrorMessage ?? "Payment could not be authorised.",
                    Status = request.Status,
                };
            }

            request.PaymentTransactionId = authorization.Value!.TransactionId;
        }

        request.Status = RmaRequestStatus.Approved;
        request.UpdatedAtUtc = clock.UtcNow;
        await rmaRepository.UpdateAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);

        return new StepResult
        {
            Outcome = StepOutcome.Continue,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Message = amount.IsZero ? "Nothing to charge." : "Payment authorised.",
            Status = request.Status,
        };
    }

    public async Task<StepResult> ConfirmAsync(Guid rmaId, CancellationToken cancellationToken)
    {
        var request = await LoadAsync(rmaId, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        // Confirmation commits the return, so it is legal only from Approved. Anything else is
        // an out-of-order or replayed page, not a request that needs a person.
        if (RefuseUnless(request, [RmaRequestStatus.Approved], "confirmation") is { } cannotConfirm)
        {
            return cannotConfirm;
        }

        request.Status = RmaRequestStatus.AwaitingShipment;
        request.UpdatedAtUtc = clock.UtcNow;
        await rmaRepository.UpdateAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);

        return new StepResult
        {
            Outcome = StepOutcome.Completed,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Message = "Return confirmed.",
            Status = request.Status,
        };
    }

    private Task<RmaRequest?> LoadAsync(Guid rmaId, CancellationToken cancellationToken) =>
        rmaRepository.GetByIdAsync(rmaId, cancellationToken);

    private static RmaEligibilityDecision Aggregate(IEnumerable<RmaEligibilityDecision> decisions)
    {
        var list = decisions.ToList();
        if (list.Count == 0)
        {
            return Denied(RmaDecisionReason.DeviceNotFound, "No items were supplied.");
        }

        // Worst-first ordering. This is the only place outcome precedence is defined.
        return list
            .OrderBy(d => d.Eligibility switch
            {
                RmaEligibility.RequiresHumanReview => 0,
                RmaEligibility.NotEligible => 1,
                RmaEligibility.Undetermined => 2,
                RmaEligibility.EligibleForPaidRepair => 3,
                _ => 4,
            })
            .ThenByDescending(d => d.MatchedRuleIds.Count)
            .First();
    }

    private static RmaEligibilityDecision Denied(RmaDecisionReason reason, string explanation) => new()
    {
        Eligibility = reason == RmaDecisionReason.DeviceNotFound
            ? RmaEligibility.NotEligible
            : RmaEligibility.RequiresHumanReview,
        Reason = reason,
        Explanation = explanation,
    };

    private static StepResult NotFound() => new()
    {
        Outcome = StepOutcome.NotEligible,
        Message = "That return could not be found.",
        Decision = Denied(RmaDecisionReason.DeviceNotFound, "That return could not be found."),
    };

    /// <summary>
    /// The state machine's single enforcement point. Every mutating step asks whether the
    /// request's current status allows it; a refusal is reported as
    /// <see cref="StepOutcome.InvalidTransition"/> rather than thrown, because it is a stale or
    /// hand-crafted page, not a server fault.
    /// </summary>
    private static StepResult? RefuseUnless(
        RmaRequest request,
        RmaRequestStatus[] allowed,
        string step)
    {
        if (allowed.Contains(request.Status))
        {
            return null;
        }

        return new StepResult
        {
            Outcome = StepOutcome.InvalidTransition,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            // Not "moved past": a draft posted straight to the payment step never reached it
            // either, and the same refusal has to read correctly for both.
            Message = $"This return is not at the {step} step, so that step cannot be completed "
                + "now. Open the return from where you left off, or start a new one.",
            Status = request.Status,
        };
    }

    /// <summary>
    /// Applies the per-product repair price table to the request: at creation, from triage when
    /// a cause turns the return into a paid repair, and again when shipping is set. Pricing at
    /// intake is what stops the payment step authorising a stale zero; re-pricing later keeps
    /// the stored amount current if the table changed while the request sat in the database.
    /// </summary>
    private void ApplyPricing(RmaRequest request, IEnumerable<Product?> products)
    {
        // Nothing is ever charged for shipping, and a covered return charges nothing at all.
        request.ShippingCharge = 0m;
        request.DepositAmount = 0m;

        if (request.Kind != RmaRequestKind.PaidRepair)
        {
            return;
        }

        var prices = products.Select(_policy.RepairPrice).ToList();

        // The currency is the price table's, never the caller's: a fee is a number in a
        // specific currency, and accepting a client-chosen code would let "69 USD" be
        // authorised as "69 JPY".
        var currencies = prices.Select(p => p.CurrencyCode.ToUpperInvariant()).Distinct().ToList();
        if (currencies.Count > 1)
        {
            throw new InvalidOperationException(
                $"Repair prices on one request use different currencies ({string.Join(", ", currencies)}); configure Policy:RepairPrices in a single currency.");
        }

        if (currencies.Count == 1)
        {
            request.CurrencyCode = currencies[0];
        }

        request.DepositAmount = prices.Sum(p => p.Fee);
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max
            ? value ?? string.Empty
            : string.Concat(value.AsSpan(0, max - 1), "…");

    /// <summary>
    /// Commits the changes staged on the context this request's repositories share.
    ///
    /// Repositories deliberately track but do not commit, so that a workflow step that touches
    /// several repositories is one transaction. Every step that mutates state ends by calling
    /// this; without it the writes sit in the change tracker and are discarded when the scope
    /// is disposed.
    /// </summary>
    private async Task CommitAsync(CancellationToken cancellationToken)
    {
        var written = await unitOfWork.SaveChangesAsync(cancellationToken);

        if (written == 0)
        {
            logger.LogWarning("A workflow step committed no rows. This usually means the step "
                + "was a no-op, or the request it changed was not tracked.");
        }
    }
}

/// <summary>Abstraction over the system clock so policy dates are testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateOnly Today { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}
