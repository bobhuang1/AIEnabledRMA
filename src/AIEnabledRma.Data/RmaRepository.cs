using System.Globalization;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Rma;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Data;

public sealed class RmaRepository(RmaDbContext db) : IRmaRepository
{
    private readonly RmaDbContext _db = db;

    public async Task AddAsync(RmaRequest request, CancellationToken cancellationToken) =>
        await _db.RmaRequests.AddAsync(request, cancellationToken);

    public Task<RmaRequest?> GetByNumberAsync(string rmaNumber, CancellationToken cancellationToken) =>
        LoadQuery().FirstOrDefaultAsync(r => r.RmaNumber == rmaNumber, cancellationToken);

    public Task<RmaRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        LoadQuery().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<RmaRequest>> GetForCustomerAsync(
        Guid customerId,
        CancellationToken cancellationToken) =>
        await LoadQuery()
            .Where(r => r.CustomerId == customerId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RmaRequest>> GetOpenForDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken) =>
        await LoadQuery()
            .Where(r => r.Lines.Any(l => l.DeviceId == deviceId)
                        && r.Status != RmaRequestStatus.Cancelled
                        && r.Status != RmaRequestStatus.Closed
                        && r.Status != RmaRequestStatus.Rejected)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Allocates the next reference for the year from a database sequence, padded to five
    /// digits so the strings sort lexicographically in the same order they sort numerically.
    /// </summary>
    /// <remarks>
    /// The year is a naming convention only. The sequence is a single global counter, so
    /// numbering stays strictly increasing across a year boundary instead of restarting and
    /// colliding with a reference that was allocated on 31 December.
    /// </remarks>
    public async Task<string> NextRmaNumberAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"RMA-{today.Year}-");

        // nextval is atomic and never repeats, so concurrent requests are safe by construction.
        // The old implementation read the highest stored number and added one, which returned
        // the same value to every request that raced between its read and its insert.
        var next = await _db.Database
            .SqlQueryRaw<int>("SELECT nextval('rma_number_seq') AS \"Value\"")
            .SingleAsync(cancellationToken);

        // The unique index on rma_number is the backstop for a number that is somehow already
        // present. Failing loudly beats issuing a duplicate reference to a second customer.
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{next:D5}");
    }

    /// <remarks>
    /// Tracked, deliberately. A step loads the aggregate, changes it and commits, and EF Core then
    /// writes the columns that actually changed.
    /// <para>
    /// The alternative, which this used to do, was to hand out a no-tracking graph and have the
    /// workflow re-attach it with <c>Update()</c>. That marks every entity in the graph Modified, so
    /// an RMA step rewrote the customer, address, device and product rows beside it, and it failed
    /// outright with "another instance with the same key value is already being tracked" whenever
    /// the customer had been loaded through the customer repository earlier in the same request —
    /// which is exactly what picking a stored address on the shipping step does.
    /// </para>
    /// Lookups that only read a list (customer search) stay no-tracking; anything a step can
    /// change is loaded here.
    /// </remarks>
    private IQueryable<RmaRequest> LoadQuery() =>
        _db.RmaRequests
            .Include(r => r.Lines)
                .ThenInclude(l => l.Device)
                    .ThenInclude(d => d!.Product)
            // Warranties come with the device because the triage step re-runs the policy
            // against the recorded fault description. Without them every re-evaluation would
            // see an uncovered unit and turn a free return into a paid repair.
            .Include(r => r.Lines)
                .ThenInclude(l => l.Device)
                    .ThenInclude(d => d!.Warranties)
            .Include(r => r.Customer)
            .Include(r => r.ShipToAddress)
            .Include(r => r.Exclusions.OrderBy(e => e.SortOrder))
            .AsSplitQuery();
}
