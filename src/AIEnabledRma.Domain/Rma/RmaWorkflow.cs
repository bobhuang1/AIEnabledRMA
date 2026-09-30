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

    Task<StepResult> ApplyTriageAsync(Guid rmaId, TriageResult triage, CancellationToken cancellationToken);

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
        CancellationToken cancellationToken)
    {
        var request = await LoadAsync(rmaId, cancellationToken);

        if (request is null)
        {
            return NotFound();
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
        }

        await rmaRepository.UpdateAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);

        return new StepResult
        {
            Outcome = triage.Verdict.Resolved
                ? StepOutcome.ResolvedByTroubleshooting
                : StepOutcome.Continue,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Message = triage.Verdict.Resolved
                ? "Troubleshooting resolved the issue, so no return is needed."
                : "Troubleshooting confirmed the fault. Please continue with the return.",
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
        if (request.Kind == RmaRequestKind.PaidRepair)
        {
            var repairFee = request.Lines.Sum(l => _policy.PriceForRepair(l.Device?.Product));
            request.ShippingCharge = 0m;
            request.DepositAmount = repairFee;
        }
        else
        {
            request.ShippingCharge = 0m;
            request.DepositAmount = 0m;
        }

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

        var amount = new Domain.Common.Money(
            request.ShippingCharge + request.DepositAmount,
            request.CurrencyCode);

        if (amount.IsZero)
        {
            request.Status = RmaRequestStatus.Approved;
        request.UpdatedAtUtc = clock.UtcNow;
        await rmaRepository.UpdateAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);


            return new StepResult
            {
                Outcome = StepOutcome.Continue,
                RmaId = request.Id,
                RmaNumber = request.RmaNumber,
                Message = "Nothing to charge.",
                Status = request.Status,
            };
        }

        var result = await paymentGateway.AuthorizeAsync(
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

        if (result.IsFailure)
        {
            return new StepResult
            {
                Outcome = StepOutcome.RequiresHumanReview,
                RmaId = request.Id,
                RmaNumber = request.RmaNumber,
                Message = result.ErrorMessage ?? "Payment could not be authorised.",
                Status = request.Status,
            };
        }

        request.PaymentTransactionId = result.Value!.TransactionId;
        request.Status = RmaRequestStatus.Approved;
        request.UpdatedAtUtc = clock.UtcNow;
        await rmaRepository.UpdateAsync(request, cancellationToken);
        await CommitAsync(cancellationToken);

        return new StepResult
        {
            Outcome = StepOutcome.Continue,
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Message = "Payment authorised.",
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

        if (request.Status != RmaRequestStatus.Approved)
        {
            return new StepResult
            {
                Outcome = StepOutcome.RequiresHumanReview,
                RmaId = request.Id,
                RmaNumber = request.RmaNumber,
                Message = "This return is not ready to be confirmed yet.",
                Status = request.Status,
            };
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
