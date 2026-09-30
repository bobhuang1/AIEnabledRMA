using System.ComponentModel;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Rules;
using ModelContextProtocol.Server;

namespace AIEnabledRma.Mcp.Tools;

/// <summary>
/// Read-only coverage and fee tools.
///
/// These exist so an agent answers "is this covered?" and "what would it cost?" with the same
/// deterministic answers the RMA workflow would give, rather than asking a model. The model may
/// explain a decision; it may not make one.
///
/// Every tool here is a pure function of stored data and policy configuration: no writes, no
/// state, no way to create, approve, or modify a return.
/// </summary>
[McpServerToolType]
public sealed class PolicyTools(
    IDeviceRepository devices,
    IEligibilityService eligibility,
    Microsoft.Extensions.Options.IOptions<RmaPolicyOptions> policyOptions)
{
    [McpServerTool(
        Name = "check_warranty_coverage",
        Title = "Check warranty coverage for a device")]
    [Description(
        "Deterministically evaluate warranty coverage for one device, identified by serial "
        + "number, MAC, IMEI, or asset tag. Returns the eligibility outcome, the reason, and "
        + "the policy rules that fired. Use this to answer a coverage question; do not answer "
        + "coverage questions from the model. This is an evaluation, not an approval: a return "
        + "is only created by the RMA workflow, and only after the fault is confirmed.")]
    public async Task<CoverageResponse> CheckCoverageAsync(
        [Description("The serial number, MAC address, IMEI, or asset tag of the unit.")]
        string identifier,
        [Description("Today's date as yyyy-MM-dd. Required, so the answer is reproducible.")]
        string today,
        [Description("How the customer describes what happened, e.g. 'it fell off a table'. Matched against the excluded-cause rules.")]
        string? problemDescription = null,
        [Description("Two-letter region code, if your deployment routes some regions to a human.")]
        string? regionCode = null,
        [Description("Whether proof of purchase is on file. Omit to let the policy decide.")]
        bool? proofOfPurchaseOnFile = null,
        [Description("Whether the unit already has an open return. Omit to read this from the database, which is authoritative.")]
        bool? hasOpenReturn = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        if (!DateOnly.TryParse(today, out var asOf))
        {
            return CoverageResponse.Invalid("today must be an ISO yyyy-MM-dd date.");
        }

        var lookup = await devices.LookupAsync(identifier, asOf, cancellationToken);

        if (lookup.Device is null)
        {
            // An unknown serial is not "probably in warranty". The device does not exist, so
            // there is nothing to be covered.
            return new CoverageResponse
            {
                Valid = true,
                Identified = false,
                IsEligible = false,
                Eligibility = RmaEligibility.NotEligible.ToString(),
                Reason = RmaDecisionReason.DeviceNotFound.ToString(),
                Explanation =
                    "That identifier does not match any unit in our catalog, so coverage "
                    + "cannot be confirmed. Check the serial number, or escalate to a "
                    + "specialist if the unit is genuinely unknown to us.",
                MatchedRuleIds = [],
                AllowsRmaCreation = false,
            };
        }

        // Honour the "was it bought" signal the caller supplied, and default to the cautious
        // value when it said nothing.
        var proof = proofOfPurchaseOnFile ?? true;

        // Ask the database whether this unit has an open return rather than assuming. An
        // earlier version defaulted this to true, which made the tool tell a customer
        // "there is already an open return for this item" for units that had none. A tool
        // that states a false fact to a customer is worse than one that asks. The caller's
        // value still wins when it explicitly supplies one, so a caller holding fresher
        // information than this database (an in-flight session, say) can override it.
        var hasOpen = hasOpenReturn
            ?? await devices.HasOpenRequestAsync(lookup.Device.Id, cancellationToken);

        var decision = eligibility.Evaluate(new RmaPolicyRequest
        {
            Device = lookup.Device,
            Today = asOf,
            RegionCode = regionCode,
            ProblemDescription = problemDescription,
            HasOpenRequestForDevice = hasOpen,
            ProofOfPurchaseOnFile = proof,

            // Never passed. The policy ignores AI confidence while AiIsAdvisoryOnly is true,
            // and this tool has no way to obtain a trustworthy value, so it does not pretend
            // to. A caller must not be able to set this field through a tool.
        });

        return new CoverageResponse
        {
            Valid = true,
            Identified = true,
            DeviceId = lookup.Device.Id,
            SerialNumber = lookup.Device.SerialNumber,
            WarrantyPlan = lookup.GoverningWarranty?.PlanName,
            WarrantyTier = lookup.GoverningWarranty?.Tier,
            WarrantyEndsOn = lookup.GoverningWarranty?.EndDate,
            IsEligible = decision.AllowsRmaCreation,
            Eligibility = decision.Eligibility.ToString(),
            Reason = decision.Reason.ToString(),
            Explanation = decision.Explanation,
            MatchedRuleIds = decision.MatchedRuleIds,
            AllowsRmaCreation = decision.AllowsRmaCreation,
            Note =
                "This is a policy evaluation, not an approval. A return is only created by the "
                + "RMA workflow after the customer's problem has been confirmed by "
                + "troubleshooting.",
        };
    }

    [McpServerTool(
        Name = "get_fee_quote",
        Title = "Quote the repair price for a device")]
    [Description(
        "Quote what it would cost to repair one unit, identified by serial number, MAC, "
        + "IMEI, or asset tag. The price comes from the per-product repair price table, the "
        + "same table the RMA workflow charges against, so the quote and the actual charge can "
        + "never disagree. Coverage is reported too: a covered unit owes nothing for a free "
        + "replacement, and the price shown is what the repair would cost without coverage. "
        + "The quote is informational; no charge is made by calling it.")]
    public async Task<FeeQuoteResponse> GetFeeQuoteAsync(
        [Description("The serial number, MAC address, IMEI, or asset tag of the unit.")]
        string identifier,
        [Description("Today's date as yyyy-MM-dd. Required, so the answer is reproducible.")]
        string today,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return FeeQuoteResponse.Invalid("identifier is required.");
        }

        if (!DateOnly.TryParse(today, out var asOf))
        {
            return FeeQuoteResponse.Invalid("today must be an ISO yyyy-MM-dd date.");
        }

        var lookup = await devices.LookupAsync(identifier, asOf, cancellationToken);
        if (lookup.Device is null)
        {
            // An unknown serial cannot be quoted a price: pricing belongs to a product, and
            // there is no product to price.
            return FeeQuoteResponse.Invalid(
                "That identifier does not match any unit in our catalog, so no repair price "
                + "can be quoted. Confirm the identifier before quoting.");
        }

        var policy = policyOptions.Value;
        var tier = policy.GetTier(lookup.GoverningWarranty?.Tier);
        var (fee, currency) = policy.RepairPrice(lookup.Device.Product);
        var covered = lookup.GoverningWarranty is not null && tier.IsCovered;

        return new FeeQuoteResponse
        {
            Valid = true,
            ProductSku = lookup.Device.Product?.Sku,
            ProductName = lookup.Device.Product?.Name,
            SerialNumber = lookup.Device.SerialNumber,
            WarrantyTier = lookup.GoverningWarranty?.Tier,
            PlanIsCovered = covered,
            RepairFee = fee,
            DepositAmount = 0m,
            ListPriceRepairFee = policy.DefaultRepairFee,
            CurrencyCode = currency,
            TurnaroundBusinessDays = lookup.GoverningWarranty is null
                ? policy.GetTier(policy.DefaultTier).TurnaroundBusinessDays
                : tier.TurnaroundBusinessDays,
            ReplacementTier = tier.ReplacementTier,
            Note = covered
                ? "This unit is covered: a free replacement applies and nothing is owed. "
                  + "The repair fee shown is what the same repair would cost out of coverage."
                : "This unit is not covered: a paid repair applies and the repair fee shown "
                  + "is what is charged when the return is confirmed.",
        };
    }
}

public sealed record CoverageResponse
{
    public required bool Valid { get; init; }

    public string? ValidationError { get; init; }

    /// <summary>False when the identifier did not resolve to a unit in the catalog.</summary>
    public bool Identified { get; init; }

    public Guid? DeviceId { get; init; }

    public string? SerialNumber { get; init; }

    public string? WarrantyPlan { get; init; }

    public string? WarrantyTier { get; init; }

    public DateOnly? WarrantyEndsOn { get; init; }

    public required bool IsEligible { get; init; }

    public required string Eligibility { get; init; }

    public string? Reason { get; init; }

    public string? Explanation { get; init; }

    public IReadOnlyList<string> MatchedRuleIds { get; init; } = [];

    /// <summary>
    /// Always false from this tool. Only the RMA workflow may create a return, so an agent
    /// reading this field can never mistake an evaluation for an approval.
    /// </summary>
    public bool AllowsRmaCreation { get; init; }

    public string? Note { get; init; }

    public static CoverageResponse Invalid(string error) => new()
    {
        Valid = false,
        ValidationError = error,
        Identified = false,
        IsEligible = false,
        Eligibility = RmaEligibility.RequiresHumanReview.ToString(),
        Reason = RmaDecisionReason.Undetermined.ToString(),
        Explanation = error,
        AllowsRmaCreation = false,
    };
}

public sealed record FeeQuoteResponse
{
    public required bool Valid { get; init; }

    public string? ValidationError { get; init; }

    public string? ProductSku { get; init; }

    public string? ProductName { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>Tier of the unit's governing warranty, or null when the unit has none.</summary>
    public string? WarrantyTier { get; init; }

    /// <summary>Whether an active warranty covers the unit, making any repair free.</summary>
    public bool PlanIsCovered { get; init; }

    /// <summary>What the customer owes to have this unit repaired out of coverage.</summary>
    public decimal RepairFee { get; init; }

    /// <summary>Always zero: repairs are charged once, as a single repair fee.</summary>
    public decimal DepositAmount { get; init; }

    /// <summary>
    /// The price applied when a product has no configured price row. Reported as a reference:
    /// the operative price is <see cref="RepairFee"/>.
    /// </summary>
    public decimal ListPriceRepairFee { get; init; }

    public string? CurrencyCode { get; init; }

    public int TurnaroundBusinessDays { get; init; }

    public string? ReplacementTier { get; init; }

    public string? Note { get; init; }

    public static FeeQuoteResponse Invalid(string error) => new()
    {
        Valid = false,
        ValidationError = error,
        PlanIsCovered = false,
        CurrencyCode = "USD",
    };
}
