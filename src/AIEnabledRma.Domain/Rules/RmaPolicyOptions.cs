using System.ComponentModel.DataAnnotations;
using AIEnabledRma.Domain.Catalog;

namespace AIEnabledRma.Domain.Rules;

/// <summary>
/// Configuration-driven warranty and eligibility policy. Everything a deployment needs to
/// change to fit its own business can be set here, so adding a tier, a return window, or an
/// excluded cause never requires editing C#.
/// </summary>
public sealed class RmaPolicyOptions
{
    public const string SectionName = "RmaPolicy";

    /// <summary>
    /// When true, the AI may only reduce the set of eligible outcomes. This is the lockdown
    /// switch and should stay true in production. It is configurable only so the
    /// deterministic path can be regression-tested in isolation.
    /// </summary>
    public bool AiIsAdvisoryOnly { get; set; } = true;

    /// <summary>
    /// Fail-closed behaviour: if the AI layer errors, times out, or returns something that
    /// fails schema validation, triage resolves to this outcome instead of proceeding.
    /// </summary>
    public RmaEligibility FallbackEligibility { get; set; } = RmaEligibility.RequiresHumanReview;

    public RmaDecisionReason FallbackReason { get; set; } = RmaDecisionReason.LowConfidenceTriage;

    /// <summary>Days after warranty end during which a return is still accepted.</summary>
    [Range(0, 3650)]
    public int GracePeriodDays { get; set; }

    /// <summary>Absolute return window from the device's ship date, independent of warranty.</summary>
    [Range(0, 3650)]
    public int ReturnWindowDays { get; set; } = 730;

    /// <summary>Minimum AI triage confidence required before an RMA may be auto-approved.</summary>
    [Range(0, 1)]
    public double MinimumAutoApproveConfidence { get; set; } = 0.75;

    /// <summary>
    /// Below this confidence the flow is routed to a human even if the policy would allow it.
    /// </summary>
    [Range(0, 1)]
    public double HumanReviewConfidenceThreshold { get; set; } = 0.45;

    /// <summary>
    /// The configured commercial tier for evaluating coverage. Never read for pricing: a repair
    /// is priced from <see cref="RepairPrices"/> so the charge follows the product, not the plan
    /// the unit happens to be filed under.
    /// </summary>
    public List<WarrantyTierPolicy> Tiers { get; set; } = [];

    public string DefaultTier { get; set; } = "standard";

    /// <summary>
    /// Per-product repair prices for out-of-warranty units. Keyed by SKU first, then product
    /// name, with <see cref="DefaultRepairFee"/> as the fallback so an unlisted product still
    /// has a quotable price. One source of truth for both the RMA workflow and the fee-quoting
    /// MCP tool, so they can never disagree about a charge.
    /// </summary>
    public List<ProductRepairPrice> RepairPrices { get; set; } = [];

    /// <summary>What an out-of-warranty repair costs when the product has no price row.</summary>
    public decimal DefaultRepairFee { get; set; }

    /// <summary>
    /// Resolves the price charged to repair <paramref name="product"/>. Matches a configured
    /// row by SKU, then by product name (both case-insensitive), then falls back to
    /// <see cref="DefaultRepairFee"/> so evaluation never throws on an unlisted product.
    /// </summary>
    public decimal PriceForRepair(Product? product) => RepairPrice(product).Fee;

    /// <summary>Price plus the currency of the matched row. Used by the fee-quoting tool,
    /// which must not invent a currency when it reports a price.</summary>
    public (decimal Fee, string CurrencyCode) RepairPrice(Product? product)
    {
        if (product is not null)
        {
            var bySku = RepairPrices.FirstOrDefault(p =>
                p.Sku is not null
                && string.Equals(p.Sku, product.Sku, StringComparison.OrdinalIgnoreCase));
            if (bySku is not null)
            {
                return (bySku.RepairFee, bySku.CurrencyCode);
            }

            var byName = RepairPrices.FirstOrDefault(p =>
                p.ProductName is not null
                && string.Equals(p.ProductName, product.Name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return (byName.RepairFee, byName.CurrencyCode);
            }
        }

        return (DefaultRepairFee, "USD");
    }

    /// <summary>
    /// Symptoms that make a device ineligible regardless of warranty. Matched against the
    /// problem description and the AI's extracted causes using
    /// <see cref="ProblemCauseRule.MatchMode"/>.
    /// </summary>
    public List<ProblemCauseRule> ExcludedCauses { get; set; } = [];

    /// <summary>Regions where an RMA cannot be auto-approved and always needs a human.</summary>
    public List<string> HumanReviewOnlyRegions { get; set; } = [];

    /// <summary>
    /// Serial-number prefixes reserved by your fraud team. A match escalates rather than denies,
    /// because a false positive should not silently discard a legitimate customer's return.
    /// </summary>
    public List<string> FraudIndicatorSerialPrefixes { get; set; } = [];

    /// <summary>
    /// Resolves a tier for evaluation, falling back to the default tier when the stored
    /// device record names a tier that is not configured. Lenient on purpose: a legacy or
    /// mistyped tier on a device row must not make every return unevaluable, and the
    /// evaluator's job is to decide, not to quote.
    ///
    /// Do not use this to answer a question about what something costs. Use
    /// <see cref="TryGetTier"/> there, because silently answering a "premium" question with
    /// standard-tier numbers is a false answer about money.
    /// </summary>
    public WarrantyTierPolicy GetTier(string? tier) =>
        TryGetTier(tier)
        ?? Tiers.FirstOrDefault(t => string.Equals(t.Name, DefaultTier, StringComparison.OrdinalIgnoreCase))
        ?? new WarrantyTierPolicy();

    /// <summary>
    /// Exact, case-insensitive tier lookup. Returns null when the tier is not configured.
    /// </summary>
    public WarrantyTierPolicy? TryGetTier(string? tier) =>
        string.IsNullOrWhiteSpace(tier)
            ? null
            : Tiers.FirstOrDefault(t => string.Equals(t.Name, tier.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Per-tier commercial rules: what coverage provides. Prices deliberately live outside this
/// type, in <see cref="RmaPolicyOptions.RepairPrices"/>; a tier describes the plan, a price
/// describes the product.
/// </summary>
public sealed class WarrantyTierPolicy
{
    public const string Standard = "standard";

    [Required]
    public string Name { get; set; } = Standard;

    /// <summary>Processing time in business days, used for customer-facing estimates.</summary>
    [Range(0, 365)]
    public int TurnaroundBusinessDays { get; set; } = 10;

    /// <summary>Which replacement tier the customer receives. Extend by adding entries here.</summary>
    public string ReplacementTier { get; set; } = "standard";

    public bool IsCovered { get; set; } = true;

    public string CurrencyCode { get; set; } = "USD";
}

/// <summary>
/// A repair price for one product. Matching is by SKU or product name, never by device, so an
/// entire family shares a price. Prices are fake in this build: they exist to make the paid
/// repair path reachable and visibly different per product.
/// </summary>
public sealed record ProductRepairPrice
{
    /// <summary>Product SKU, matched case-insensitively. Takes precedence over the name.</summary>
    public string? Sku { get; init; }

    /// <summary>Product name, matched case-insensitively when the SKU does not match.</summary>
    public string? ProductName { get; init; }

    public decimal RepairFee { get; init; }

    public string CurrencyCode { get; init; } = "USD";
}

/// <summary>
/// How an excluded cause is matched against free text.
/// </summary>
public enum CauseMatchMode
{
    /// <summary>Any of the phrases appears as a whole word (case-insensitive).</summary>
    AnyTerm = 0,

    /// <summary>All phrases must be present.</summary>
    AllTerms = 1,

    /// <summary>The text contains the phrase as a substring.</summary>
    Contains = 2,
}

public sealed class ProblemCauseRule
{
    [Required]
    public string RuleId { get; set; } = string.Empty;

    [Required]
    public string Description { get; set; } = string.Empty;

    public List<string> Terms { get; set; } = [];

    public CauseMatchMode MatchMode { get; set; } = CauseMatchMode.AnyTerm;

    public RmaEligibility ResultingEligibility { get; set; } = RmaEligibility.NotEligible;

    public RmaDecisionReason ResultingReason { get; set; } = RmaDecisionReason.ExcludedCause;

    public bool IsActive { get; set; } = true;
}
