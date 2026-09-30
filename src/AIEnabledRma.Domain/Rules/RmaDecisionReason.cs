namespace AIEnabledRma.Domain.Rules;

/// <summary>
/// Why an RMA was, or was not, allowed. Enumerating the reasons is what lets the
/// AI layer stay advisory: it may only *narrow* these outcomes, never invent one.
/// </summary>
public enum RmaDecisionReason
{
    Undetermined = 0,

    /// <summary>Device is within an active warranty and troubleshooting did not resolve it.</summary>
    InWarrantyConfirmedDefect = 1,

    /// <summary>Troubleshooting steps from the knowledge base resolved the reported symptom.</summary>
    ResolvedByTroubleshooting = 2,

    /// <summary>No active warranty covers the device.</summary>
    OutOfWarranty = 3,

    /// <summary>Damage consistent with an excluded cause (physical impact, liquid ingress).</summary>
    ExcludedCause = 4,

    /// <summary>Warranty window exceeded.</summary>
    WarrantyExpired = 5,

    /// <summary>Device is already covered by an open RMA.</summary>
    DuplicateOpenRequest = 6,

    /// <summary>Return is outside the configured return window.</summary>
    OutsideReturnWindow = 7,

    /// <summary>Device or serial is not known to the catalog.</summary>
    DeviceNotFound = 8,

    /// <summary>Purchase proof could not be established.</summary>
    ProofOfPurchaseMissing = 9,

    /// <summary>Serial shows signs of tampering and needs a human decision.</summary>
    SuspectedFraud = 10,

    /// <summary>Business hours or region restrictions; routed to a human.</summary>
    EscalatedToAgent = 11,

    /// <summary>AI triage could not reach a confident answer and policy requires a human.</summary>
    LowConfidenceTriage = 12,
}

/// <summary>
/// Whether the deterministic policy layer permits an RMA to be created.
/// </summary>
public enum RmaEligibility
{
    /// <summary>Not enough information to decide yet.</summary>
    Undetermined = 0,

    /// <summary>Covered by an active warranty; the customer is owed a free repair or replacement.</summary>
    Eligible = 1,

    /// <summary>No repair path at all: cause excluded, outside the return window, unknown device.</summary>
    NotEligible = 2,

    /// <summary>Needs a human decision before the flow can continue.</summary>
    RequiresHumanReview = 3,

    /// <summary>
    /// Not covered by any warranty, but the device itself is valid: the customer may still
    /// be quoted a paid repair. The wizard treats this like <see cref="Eligible"/>, except
    /// that a payment is collected before anything ships.
    /// </summary>
    EligibleForPaidRepair = 4,
}

/// <summary>
/// An eligibility outcome plus the reason and the evidence that produced it.
/// Evidence is retained for the audit trail and shown to support staff.
/// </summary>
public sealed record RmaEligibilityDecision
{
    public required RmaEligibility Eligibility { get; init; }

    public required RmaDecisionReason Reason { get; init; }

    /// <summary>Human-readable explanation, safe to show to the customer or to staff.</summary>
    public required string Explanation { get; init; }

    /// <summary>Identifiers of the policy rules that fired, for audit and debugging.</summary>
    public IReadOnlyList<string> MatchedRuleIds { get; init; } = [];

    /// <summary>When true, the wizard may proceed to create an RMA (free or paid).</summary>
    public bool AllowsRmaCreation => Eligibility is RmaEligibility.Eligible or RmaEligibility.EligibleForPaidRepair;
}
