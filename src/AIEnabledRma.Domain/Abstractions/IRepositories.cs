using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;

namespace AIEnabledRma.Domain.Abstractions;

/// <summary>
/// Catalog and device lookups. Backed by PostgreSQL with trigram indexes, and fuzzy on
/// every free-text field so a customer who types "josephin smyth" still finds "Josephine Smith".
/// </summary>
public interface IDeviceRepository
{
    Task<Device?> GetBySerialAsync(string serialNumber, CancellationToken cancellationToken);

    Task<IReadOnlyList<Device>> FindByIdentifierAsync(
        string identifier,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Device>> SearchAsync(
        DeviceSearchQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Device plus the warranty that governs today, in one round trip. Falls back to fuzzy
    /// matching, so a truncated, mistyped, or product-name-shaped identifier can still resolve.
    /// </summary>
    /// <remarks>
    /// Agent and operator paths use this. The customer-facing wizard must not: see
    /// <see cref="LookupExactAsync"/>.
    /// </remarks>
    Task<DeviceLookupResult> LookupAsync(
        string identifier,
        DateOnly today,
        CancellationToken cancellationToken);

    /// <summary>
    /// Device plus the warranty that governs today, matched only on an exact serial or an exact
    /// alternate identifier (MAC, IMEI, host serial). Never fuzzy, never a catalog scan.
    /// </summary>
    /// <remarks>
    /// Intake uses this because fuzzy matching there was an enumeration oracle: an anonymous
    /// visitor could walk the catalog a serial prefix at a time, and every hit opened a return
    /// against the real unit, which then blocked its owner as a duplicate. Tools that serve an
    /// operator who already holds partial information keep <see cref="LookupAsync"/>.
    /// </remarks>
    Task<DeviceLookupResult> LookupExactAsync(
        string identifier,
        DateOnly today,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether another return for this unit has reached the point where the unit is expected
    /// back — AwaitingShipment through Repaired — ignoring <paramref name="excludingRequestId"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than "not cancelled, closed or rejected". A return in Draft,
    /// PendingPayment, Approved or NeedsReview is still being filled in, and the wizard is
    /// anonymous, so counting those let any visitor hold a unit by starting a return and walking
    /// away from it: the owner then met the duplicate rule instead. Intake asks this with nothing
    /// excluded, and confirmation asks it again with its own id excluded, because that is the
    /// moment the request joins the set.
    /// </remarks>
    Task<bool> HasReturnInProgressAsync(
        Guid deviceId,
        Guid? excludingRequestId,
        CancellationToken cancellationToken);
}

public sealed record DeviceSearchQuery
{
    public string? SerialNumber { get; init; }

    public string? MacAddress { get; init; }

    public string? Imei { get; init; }

    public string? Sku { get; init; }

    public string? ProductName { get; init; }

    public string? Model { get; init; }

    public int Take { get; init; } = 25;
}

public sealed record DeviceLookupResult
{
    public Device? Device { get; init; }

    public Warranty? GoverningWarranty { get; init; }

    public bool IsInWarranty { get; init; }

    /// <summary>
    /// The identifier the caller supplied, trimmed, as typed. Carried on a miss as well as a
    /// hit, because a refusal can only tell the customer to check their typing if it can name
    /// what they typed.
    /// </summary>
    public string? Identifier { get; init; }

    public string? MatchedOn { get; init; }

    public double MatchScore { get; init; }
}

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The customer's name alone, projected so no entity is tracked.
    /// </summary>
    /// <remarks>
    /// <see cref="GetByIdAsync"/> returns a tracked graph, and attaching a second copy of the
    /// same customer later in the same request throws "already being tracked". A caller that
    /// only wants to know what to redact must not drag the entity into the change tracker to
    /// find out.
    /// </remarks>
    Task<CustomerName?> GetNameAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Customer>> SearchAsync(
        CustomerSearchQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the shipping address the RMA should use: the customer's default, or the best
    /// fuzzy match for a free-text address the customer typed during the wizard.
    ///
    /// Returns null only when the customer has no address at all. A weak free-text match
    /// still yields an address (the default), but with <see cref="AddressResolution.IsFallback"/>
    /// set, because silently shipping to the wrong site is worse than shipping to the
    /// customer's default and asking them to confirm.
    /// </summary>
    Task<AddressResolution?> ResolveAddressAsync(
        Guid customerId,
        string? freeTextAddress,
        CancellationToken cancellationToken);
}

public sealed record CustomerName(string FirstName, string LastName);

/// <summary>Outcome of resolving a shipping address, including how confident the match was.</summary>
public sealed record AddressResolution
{
    public required Address Address { get; init; }

    /// <summary>Similarity of the free-text input to the chosen address, 0..1.</summary>
    public required double Score { get; init; }

    /// <summary>
    /// True when the free-text input did not match confidently and the customer's default
    /// address was substituted. Callers must show the address for confirmation in this case.
    /// </summary>
    public bool IsFallback { get; init; }

    /// <summary>
    /// True when the address was taken directly from the customer's default with no free-text
    /// input to score.
    /// </summary>
    public bool IsDefault { get; init; }
}

public sealed record CustomerSearchQuery
{
    public string? Name { get; init; }

    public string? Email { get; init; }

    public string? PhoneNumber { get; init; }

    public int Take { get; init; } = 25;
}

/// <summary>
/// Persistence for RMA requests. Kept behind an interface so the workflow can be unit-tested
/// without a database and so a deployment can swap in its own store.
/// </summary>
/// <remarks>
/// There is no update call: a loaded aggregate is tracked by the caller's unit of work, so a step
/// changes it in place and commits with <see cref="IUnitOfWork.SaveChangesAsync"/>. A re-attach
/// API invites the caller to hand back a graph whose neighbouring rows it never meant to write.
/// </remarks>
public interface IRmaRepository
{
    Task AddAsync(RmaRequest request, CancellationToken cancellationToken);

    Task<RmaRequest?> GetByNumberAsync(string rmaNumber, CancellationToken cancellationToken);

    Task<RmaRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<RmaRequest>> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RmaRequest>> GetOpenForDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>Allocates the next human-facing reference. Implementations must be retry-safe.</summary>
    Task<string> NextRmaNumberAsync(DateOnly today, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs the deterministic eligibility rules. Surfaced separately from the repository so the
/// web layer can ask "may this proceed?" without loading an entire RMA aggregate.
/// </summary>
public interface IEligibilityService
{
    RmaEligibilityDecision Evaluate(RmaPolicyRequest request);
}
