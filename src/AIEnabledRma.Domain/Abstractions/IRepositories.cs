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

    /// <summary>Device plus the warranty that governs today, in one round trip.</summary>
    Task<DeviceLookupResult> LookupAsync(
        string identifier,
        DateOnly today,
        CancellationToken cancellationToken);

    Task<bool> HasOpenRequestAsync(Guid deviceId, CancellationToken cancellationToken);
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

    public string? MatchedOn { get; init; }

    public double MatchScore { get; init; }
}

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

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

    public string? ExternalCustomerId { get; init; }

    public int Take { get; init; } = 25;
}

/// <summary>
/// Persistence for RMA requests. Kept behind an interface so the workflow can be unit-tested
/// without a database and so a deployment can swap in its own store.
/// </summary>
public interface IRmaRepository
{
    Task AddAsync(RmaRequest request, CancellationToken cancellationToken);

    Task<RmaRequest?> GetByNumberAsync(string rmaNumber, CancellationToken cancellationToken);

    Task<RmaRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task UpdateAsync(RmaRequest request, CancellationToken cancellationToken);

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
