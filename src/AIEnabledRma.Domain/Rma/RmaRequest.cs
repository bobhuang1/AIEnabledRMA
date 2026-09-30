namespace AIEnabledRma.Domain.Rma;

public enum RmaRequestStatus
{
    Draft = 0,
    PendingPayment = 1,
    Approved = 2,
    AwaitingShipment = 3,
    Received = 4,
    InRepair = 5,
    Repaired = 6,
    ShippedToCustomer = 7,
    Closed = 8,
    Cancelled = 9,
    Rejected = 10,
}

public enum RmaRequestKind
{
    /// <summary>Covered by warranty; no customer payment expected.</summary>
    Warranty = 0,

    /// <summary>Not covered; customer is quoted a paid repair.</summary>
    PaidRepair = 1,

    /// <summary>Advance replacement shipped before the faulty unit arrives.</summary>
    AdvanceReplacement = 2,
}

/// <summary>
/// One returned item inside an RMA. A single RMA can cover several devices, which is why
/// shipping and fees are computed at the request level rather than per line.
/// </summary>
public sealed class RmaRequest
{
    public required Guid Id { get; set; }

    /// <summary>Human-facing reference, e.g. "RMA-2026-000123". Unique.</summary>
    public required string RmaNumber { get; set; }

    public Guid? CustomerId { get; set; }

    public Customers.Customer? Customer { get; set; }

    public Guid? ShipToAddressId { get; set; }

    public Customers.Address? ShipToAddress { get; set; }

    public RmaRequestStatus Status { get; set; } = RmaRequestStatus.Draft;

    public RmaRequestKind Kind { get; set; } = RmaRequestKind.Warranty;

    /// <summary>The commercial tier the policy layer selected for this request.</summary>
    public string? WarrantyTier { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public decimal ShippingCharge { get; set; }

    public decimal DepositAmount { get; set; }

    public string? PaymentTransactionId { get; set; }

    public string? EligibilityReason { get; set; }

    /// <summary>Set when the request is cancelled, so the reason survives in the audit trail.</summary>
    public string? CancellationReason { get; set; }

    public string? PolicyRuleTrace { get; set; }

    /// <summary>Free-text summary of the troubleshooting conversation, for the repair bench.</summary>
    public string? TroubleshootingSummary { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<RmaLine> Lines { get; set; } = new List<RmaLine>();

    public ICollection<RmaExclusion> Exclusions { get; set; } = new List<RmaExclusion>();
}

public sealed class RmaLine
{
    public required Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the owning request. Set by the ORM from the <see cref="RmaRequest"/>
    /// navigation, so it is deliberately not a <c>required</c> member: callers construct a
    /// line and attach it to a request, they do not assign both.
    /// </summary>
    public Guid RmaRequestId { get; set; }

    public RmaRequest? RmaRequest { get; set; }

    public required Guid DeviceId { get; set; }

    public Catalog.Device? Device { get; set; }

    /// <summary>Category chosen by the customer, optionally pre-filled by AI triage.</summary>
    public string? ProblemCategoryCode { get; set; }

    public required string ProblemDescription { get; set; }

    public string? WhatCustomerTried { get; set; }

    /// <summary>Ordered troubleshooting steps the AI recommended, joined for storage.</summary>
    public string? RecommendedStepsJson { get; set; }

    public string? TriageVerdict { get; set; }

    public double? TriageConfidence { get; set; }

    public string? TriageArticleIds { get; set; }
}

/// <summary>
/// An item that was offered for return but left out, persisted with the request so the notice
/// survives a refresh or a new session. Kept in its own table rather than as a flag on
/// <see cref="RmaLine"/> because an excluded item never became a line; trying to model it as
/// one would either deny it every line column or smuggle exclusion semantics into shipping.
/// </summary>
public sealed class RmaExclusion
{
    public required Guid Id { get; set; }

    public Guid RmaRequestId { get; set; }

    public RmaRequest? RmaRequest { get; set; }

    /// <summary>Null when the identifier did not resolve to a catalog item.</summary>
    public Guid? DeviceId { get; set; }

    public Catalog.Device? Device { get; set; }

    public string? SerialNumber { get; set; }

    /// <summary>Which field the customer supplied the item by, e.g. "serial".</summary>
    public string? MatchedOn { get; set; }

    /// <summary>Enums stored as their names, matching <see cref="RmaRequest.EligibilityReason"/>.</summary>
    public required string Eligibility { get; set; }

    public required string Reason { get; set; }

    public string? Explanation { get; set; }

    /// <summary>Preserves the order the customer supplied the items in.</summary>
    public int SortOrder { get; set; }
}
