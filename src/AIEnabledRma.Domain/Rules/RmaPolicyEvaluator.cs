using System.Text.RegularExpressions;
using AIEnabledRma.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Domain.Rules;

/// <summary>
/// The complete, non-AI input a policy evaluation needs. Deliberately a closed record:
/// adding a field makes every rule's behaviour explicit at the call site.
/// </summary>
public sealed record RmaPolicyRequest
{
    public required Device Device { get; init; }

    public required DateOnly Today { get; init; }

    public string? RegionCode { get; init; }

    /// <summary>Customer- or AI-supplied problem text, used for excluded-cause matching.</summary>
    public string? ProblemDescription { get; init; }

    public bool HasOpenRequestForDevice { get; init; }

    /// <summary>Purchase proof the deployment requires. Nullable so "not required" is explicit.</summary>
    public bool? ProofOfPurchaseOnFile { get; init; }

    /// <summary>Only used when the deployment relaxes the advisory-only lockdown. Ignored otherwise.</summary>
    public double? AiConfidence { get; init; }
}

/// <summary>
/// The domain's own port for deterministic eligibility. <see cref="IRmaPolicy"/> and
/// <see cref="AIEnabledRma.Domain.Abstractions.IEligibilityService"/> are two separate
/// abstractions over the same evaluation — one belongs to the domain, the other is the seam
/// the web and MCP layers resolve so they can ask "may this proceed?" without loading an
/// entire RMA aggregate. They are satisfied by one implementation, so the two can never
/// disagree about an outcome.
/// </summary>
public interface IRmaPolicy
{
    RmaEligibilityDecision Evaluate(RmaPolicyRequest request);
}

/// <summary>
/// Deterministic eligibility policy. Every rule here is auditable and reproducible;
/// the AI is never consulted by this layer.
/// </summary>
public sealed class RmaPolicyEvaluator(IOptions<RmaPolicyOptions> options)
    : IRmaPolicy, AIEnabledRma.Domain.Abstractions.IEligibilityService
{
    private readonly RmaPolicyOptions _options = options.Value;

    public RmaEligibilityDecision Evaluate(RmaPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fired = new List<string>();

        // 1. Unknown device: nothing to decide against.
        if (request.Device.Id == Guid.Empty)
        {
            return Deny(
                RmaDecisionReason.DeviceNotFound,
                "That item is not in our product catalog, so we cannot check its coverage.",
                fired);
        }

        // 2. Suspected serial tampering. Escalate rather than deny so a genuine customer
        //    with a mislabelled unit is not silently turned away.
        if (MatchesFraudIndicator(request.Device.SerialNumber, out var fraudRuleId))
        {
            fired.Add(fraudRuleId);
            return new RmaEligibilityDecision
            {
                Eligibility = RmaEligibility.RequiresHumanReview,
                Reason = RmaDecisionReason.SuspectedFraud,
                Explanation = "This serial number has been flagged for manual verification. "
                              + "A support specialist will take it from here.",
                MatchedRuleIds = fired,
            };
        }

        // 3. One open RMA per device. Idempotent: the customer is sent to the existing one.
        if (request.HasOpenRequestForDevice)
        {
            return new RmaEligibilityDecision
            {
                Eligibility = RmaEligibility.RequiresHumanReview,
                Reason = RmaDecisionReason.DuplicateOpenRequest,
                Explanation = "There is already an open return for this item. "
                              + "A specialist will confirm its status for you.",
                MatchedRuleIds = fired,
            };
        }

        // 4. Excluded causes, matched on problem text. Checked before warranty because a
        //    liquid-damage report is out of scope even on a brand-new in-warranty unit.
        var causeDecision = EvaluateExcludedCause(request.ProblemDescription, fired);
        if (causeDecision is not null)
        {
            return causeDecision;
        }

        // 5. Return window measured from the ship date, when known.
        if (request.Device.ShippedAtUtc is { } shippedAt)
        {
            var shippedDate = DateOnly.FromDateTime(shippedAt.UtcDateTime);
            var daysSinceShip = request.Today.DayNumber - shippedDate.DayNumber;
            if (daysSinceShip > _options.ReturnWindowDays)
            {
                return Deny(
                    RmaDecisionReason.OutsideReturnWindow,
                    $"This item was shipped more than {_options.ReturnWindowDays} days ago, "
                    + "so it falls outside our return window.",
                    fired);
            }
        }

        // 6. Proof of purchase.
        if (request.ProofOfPurchaseOnFile == false)
        {
            return new RmaEligibilityDecision
            {
                Eligibility = RmaEligibility.RequiresHumanReview,
                Reason = RmaDecisionReason.ProofOfPurchaseMissing,
                Explanation = "We could not find proof of purchase for this item. "
                              + "A specialist will review the purchase record.",
                MatchedRuleIds = fired,
            };
        }

        // 7. Warranty, including the grace period.
        var warranty = SelectWarranty(request.Device, request.Today);
        if (warranty is null)
        {
            // Out of warranty is not a refusal: it routes to the paid-repair path. Denying
            // outright used to turn an expired unit into a dead end even though the device is
            // perfectly repairable, and the enum docs always said a paid repair was available.
            fired.Add("warranty.expired");
            return new RmaEligibilityDecision
            {
                Eligibility = RmaEligibility.EligibleForPaidRepair,
                Reason = RmaDecisionReason.OutOfWarranty,
                Explanation = "This item is not currently covered by a warranty, so a free "
                              + "replacement is not available. It can still be repaired for a fee.",
                MatchedRuleIds = fired,
            };
        }

        var tier = _options.GetTier(warranty.Tier);
        if (!tier.IsCovered)
        {
            fired.Add($"warranty.tier:{tier.Name}");
            return new RmaEligibilityDecision
            {
                Eligibility = RmaEligibility.EligibleForPaidRepair,
                Reason = RmaDecisionReason.OutOfWarranty,
                Explanation = $"The '{warranty.PlanName}' plan does not include a free "
                              + "replacement. It can still be repaired for a fee.",
                MatchedRuleIds = fired,
            };
        }

        fired.Add($"warranty.tier:{tier.Name}");

        // 8. Regional restriction.
        if (!string.IsNullOrWhiteSpace(request.RegionCode)
            && _options.HumanReviewOnlyRegions.Any(
                r => string.Equals(r, request.RegionCode, StringComparison.OrdinalIgnoreCase)))
        {
            fired.Add($"region:{request.RegionCode}");
            return new RmaEligibilityDecision
            {
                Eligibility = RmaEligibility.RequiresHumanReview,
                Reason = RmaDecisionReason.EscalatedToAgent,
                Explanation = "Returns from your region are reviewed by a specialist before approval.",
                MatchedRuleIds = fired,
            };
        }

        // 9. AI confidence, honoured only when the deployment has relaxed the lockdown.
        //    Kept as the final rule so it can never bypass a denial above.
        if (!_options.AiIsAdvisoryOnly && request.AiConfidence is { } confidence)
        {
            if (confidence < _options.HumanReviewConfidenceThreshold)
            {
                fired.Add("ai.confidence.low");
                return new RmaEligibilityDecision
                {
                    Eligibility = _options.FallbackEligibility,
                    Reason = RmaDecisionReason.LowConfidenceTriage,
                    Explanation = "We could not confirm the fault automatically, so a specialist will review it.",
                    MatchedRuleIds = fired,
                };
            }
        }

        fired.Add("warranty.active");
        return new RmaEligibilityDecision
        {
            Eligibility = RmaEligibility.Eligible,
            Reason = RmaDecisionReason.InWarrantyConfirmedDefect,
            Explanation = "This item is covered and the fault has been confirmed by troubleshooting.",
            MatchedRuleIds = fired,
        };
    }

    /// <summary>
    /// Picks the warranty record that governs the decision: active, started, and still
    /// running at <paramref name="today"/> allowing for the configured grace period.
    /// Where several records overlap, the one that runs latest wins, so a superseded
    /// record on the same unit cannot shadow the live one.
    /// </summary>
    public Warranty? SelectWarranty(Device device, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(device);

        // The grace period is read from the same options object the rest of the policy uses.
        // It used to be ignored here even though it was configurable, so a deployment that
        // set a grace period still had expired-but-recent units denied outright.
        return device.Warranties
            .Where(w => w.IsActive)
            .Where(w => w.StartDate <= today.AddDays(1)
                        && w.EndDate.AddDays(_options.GracePeriodDays) >= today)
            .OrderByDescending(w => w.EndDate)
            .ThenByDescending(w => w.StartDate)
            .FirstOrDefault();
    }

    /// <summary>True when the device has a warranty that is still running at <paramref name="today"/>.</summary>
    public static bool IsInWarranty(Device device, DateOnly today, int gracePeriodDays = 0) =>
        device.Warranties.Any(w => w.IsActive
                                   && w.StartDate <= today.AddDays(1)
                                   && w.EndDate.AddDays(gracePeriodDays) >= today);

    private RmaEligibilityDecision? EvaluateExcludedCause(string? problemDescription, List<string> fired)
    {
        if (string.IsNullOrWhiteSpace(problemDescription))
        {
            return null;
        }

        foreach (var rule in _options.ExcludedCauses.Where(r => r.IsActive && r.Terms.Count > 0))
        {
            if (Matches(rule, problemDescription))
            {
                fired.Add(rule.RuleId);
                return new RmaEligibilityDecision
                {
                    Eligibility = rule.ResultingEligibility,
                    Reason = rule.ResultingReason,
                    Explanation = rule.Description,
                    MatchedRuleIds = fired,
                };
            }
        }

        return null;
    }

    private static bool Matches(ProblemCauseRule rule, string text) => rule.MatchMode switch
    {
        CauseMatchMode.AllTerms => rule.Terms.All(t => ContainsWholeWord(text, t)),
        CauseMatchMode.Contains => rule.Terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)),
        _ => rule.Terms.Any(t => ContainsWholeWord(text, t)),
    };

    /// <summary>
    /// Whole-word containment with a simple token scan. Avoids <c>Regex</c> construction on a
    /// hot path and avoids substring false positives such as "screen" matching "screened".
    /// </summary>
    private static bool ContainsWholeWord(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        var haystackTokens = Tokenize(haystack);
        var needleToken = Tokenize(needle).FirstOrDefault();
        if (needleToken is null)
        {
            return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        return haystackTokens.Contains(needleToken, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Tokenize(string value) =>
        Regex.Matches(value.ToLowerInvariant(), "[a-z0-9]+")
            .Select(m => m.Value);

    private bool MatchesFraudIndicator(string serialNumber, out string ruleId)
    {
        foreach (var prefix in _options.FraudIndicatorSerialPrefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix)
                && serialNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                ruleId = $"fraud.prefix:{prefix}";
                return true;
            }
        }

        ruleId = string.Empty;
        return false;
    }

    private static RmaEligibilityDecision Deny(
        RmaDecisionReason reason,
        string explanation,
        List<string> fired) => new()
    {
        Eligibility = RmaEligibility.NotEligible,
        Reason = reason,
        Explanation = explanation,
        MatchedRuleIds = fired,
    };
}
