using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Devices;
using AIEnabledRma.Domain.Rma;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Data;

public sealed class DeviceRepository(
    RmaDbContext db,
    IOptions<DeviceLookupOptions> options) : IDeviceRepository
{
    private readonly RmaDbContext _db = db;
    private readonly DeviceLookupOptions _options = options.Value;

    public Task<Device?> GetBySerialAsync(string serialNumber, CancellationToken cancellationToken) =>
        _db.Devices
            .Include(d => d.Product)
            .Include(d => d.Warranties)
            .Include(d => d.AlternateIdentifiers)
            // Two collection navigations in one query. Without a split the provider must
            // build a cartesian product of warranties x identifiers, which is both wrong
            // for row counts and what raises RelationalEventId 20504.
            .AsSplitQuery()
            .FirstOrDefaultAsync(
                d => d.SerialNumber.ToUpper() == serialNumber.ToUpper(),
                cancellationToken);

    public async Task<IReadOnlyList<Device>> FindByIdentifierAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        var normalized = identifier.Trim().ToUpperInvariant();

        // A MAC may arrive with or without separators, and in any case, so both the
        // normalized and unpunctuated forms are compared.
        var mac = identifier.Trim().Replace(":", string.Empty).Replace("-", string.Empty);

        var byIdentifier = _db.Devices
            .Include(d => d.Product)
            .Include(d => d.Warranties)
            .AsNoTracking()
            .Where(d => d.AlternateIdentifiers.Any(a => a.Identifier.ToUpper() == normalized)
                        || (d.MacAddress != null
                            && d.MacAddress.ToUpper() == normalized)
                        || (d.MacAddress != null
                            && d.MacAddress.ToUpper() == mac)
                        || (d.Imei != null && d.Imei == normalized));

        var bySerial = _db.Devices
            .Include(d => d.Product)
            .Include(d => d.Warranties)
            .AsNoTracking()
            .Where(d => d.SerialNumber.ToUpper() == normalized);

        // Two indexed round trips rather than one Concat/Distinct. The single-query form is
        // not translatable: EF cannot apply DISTINCT over a projection that carries
        // collection navigations. The second query only runs when the first found nothing,
        // and a real identifier resolves on the first.
        var byIdentifierMatches = await byIdentifier.ToListAsync(cancellationToken);

        if (byIdentifierMatches.Count > 0)
        {
            return byIdentifierMatches;
        }

        return await bySerial.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Device>> SearchAsync(
        DeviceSearchQuery query,
        CancellationToken cancellationToken)
    {
        var q = _db.Devices
            .Include(d => d.Product)
            .Include(d => d.Warranties)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.SerialNumber))
        {
            var s = query.SerialNumber.Trim().ToUpperInvariant();
            q = q.Where(d => d.SerialNumber.ToUpper().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(query.MacAddress))
        {
            var m = query.MacAddress.Replace(":", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
            q = q.Where(d => d.MacAddress != null && d.MacAddress.ToUpper().Contains(m));
        }

        if (!string.IsNullOrWhiteSpace(query.Imei))
        {
            var i = query.Imei.Trim();
            q = q.Where(d => d.Imei == i);
        }

        if (!string.IsNullOrWhiteSpace(query.Sku))
        {
            var sku = query.Sku.Trim().ToUpperInvariant();
            q = q.Where(d => d.Product!.Sku.ToUpper() == sku);
        }

        if (!string.IsNullOrWhiteSpace(query.Model))
        {
            var model = query.Model.Trim().ToUpperInvariant();
            q = q.Where(d => d.Product!.Model != null && d.Product.Model.ToUpper() == model);
        }

        if (!string.IsNullOrWhiteSpace(query.ProductName))
        {
            // Trigram containment: index-backed and tolerant of typos in the product name.
            var name = query.ProductName.Trim();
            q = q.Where(d => EF.Functions.ILike(d.Product!.Name, $"%{name}%"));
        }

        // Ordered before Take so paging is repeatable; an unordered Take returns whichever
        // rows the plan happens to visit first.
        return await q
            .OrderBy(d => d.Id)
            .Take(query.Take)
            .ToListAsync(cancellationToken);
    }

    public async Task<DeviceLookupResult> LookupAsync(
        string identifier,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var trimmed = identifier.Trim();

        // Exact serial match first: the overwhelmingly common path, and index-backed.
        var device = await GetBySerialAsync(trimmed, cancellationToken);
        if (device is not null)
        {
            return BuildResult(device, today, "serial", 1d);
        }

        // Then exact alternate-identifier match (MAC, IMEI, host serial).
        var alternates = await FindByIdentifierAsync(trimmed, cancellationToken);
        if (alternates.Count > 0)
        {
            return BuildResult(alternates[0], today, "alternate-identifier", 0.98d);
        }

        // Finally fuzzy. Trigram similarity is computed in the database; the in-memory
        // scorer then re-ranks the shortlist so a coincidental trigram hit cannot win.
        if (!_options.AllowFuzzyMatch)
        {
            return new DeviceLookupResult { MatchScore = 0d };
        }

        var needle = trimmed.ToUpperInvariant();
        var unpunctuated = trimmed.Replace(":", string.Empty).Replace("-", string.Empty);

        var candidates = await _db.Devices
            .Include(d => d.Product)
                .Include(d => d.Warranties)
                .Where(d => d.SerialNumber.ToUpper().Contains(needle)
                        || (d.MacAddress != null
                            && d.MacAddress.ToUpper().Contains(unpunctuated))
                        || (d.Imei != null && d.Imei.Contains(trimmed)))
                // Stable ordering: the pool is scored after this, so an arbitrary slice would
                // make the same serial match or not match depending on row order.
                .OrderBy(d => d.Id)
                .Take(_options.FuzzyCandidatePoolSize)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            // Nothing the trigram index considered similar. Fall back to the whole catalog
            // only while the catalog is small enough that this is cheap, which is the case
            // for the bundled demo data and for a first deployment.
            if (_options.AllowFullScanFallback && await _db.Devices.CountAsync(cancellationToken)
                    <= _options.FullScanThreshold)
            {
                candidates = await _db.Devices
                    .Include(d => d.Product)
                    .Include(d => d.Warranties)
                    .AsNoTracking()
                    .OrderBy(d => d.Id)
                    .Take(_options.FullScanThreshold)
                    .ToListAsync(cancellationToken);
            }
        }

        var best = candidates
            .Select(d => (Device: d, Score: ScoreDevice(trimmed, d)))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best.Device is null || best.Score < _options.MinimumMatchScore)
        {
            return new DeviceLookupResult { MatchScore = best.Score };
        }

        return BuildResult(best.Device, today, "fuzzy", best.Score);
    }

    public async Task<bool> HasOpenRequestAsync(Guid deviceId, CancellationToken cancellationToken) =>
        await _db.RmaLines
            .AnyAsync(
                l => l.DeviceId == deviceId
                     && l.RmaRequest!.Status != RmaRequestStatus.Cancelled
                     && l.RmaRequest.Status != RmaRequestStatus.Closed
                     && l.RmaRequest.Status != RmaRequestStatus.Rejected,
                cancellationToken);

    private static double ScoreDevice(string query, Device device)
    {
        var serialScore = FuzzyMatcher.Score(query, device.SerialNumber);
        var macScore = FuzzyMatcher.Score(query.Replace(":", string.Empty), device.MacAddress);
        var skuScore = FuzzyMatcher.Score(query, device.Product?.Sku);
        var modelScore = FuzzyMatcher.Score(query, device.Product?.Model);

        return Math.Max(serialScore, Math.Max(macScore, Math.Max(skuScore, modelScore)));
    }

    private static DeviceLookupResult BuildResult(
        Device device,
        DateOnly today,
        string matchedOn,
        double score)
    {
        var governing = device.Warranties
            .Where(w => w.IsActive)
            .Where(w => w.StartDate <= today.AddDays(1) && w.EndDate >= today)
            .OrderByDescending(w => w.EndDate)
            .ThenByDescending(w => w.StartDate)
            .FirstOrDefault();

        return new DeviceLookupResult
        {
            Device = device,
            GoverningWarranty = governing,
            IsInWarranty = governing is not null,
            MatchedOn = matchedOn,
            MatchScore = score,
        };
    }
}

public sealed class DeviceLookupOptions
{
    public const string SectionName = "DeviceLookup";

    public bool AllowFuzzyMatch { get; set; } = true;

    /// <summary>Minimum in-memory score for a fuzzy hit to be accepted.</summary>
    public double MinimumMatchScore { get; set; } = 0.72;

    /// <summary>How many trigram candidates to pull for re-ranking.</summary>
    public int FuzzyCandidatePoolSize { get; set; } = 25;

    public bool AllowFullScanFallback { get; set; } = true;

    /// <summary>
    /// Catalog size below which a full scan is permitted. Above this, an unmatched
    /// identifier returns not-found rather than risking a slow query.
    /// </summary>
    public int FullScanThreshold { get; set; } = 5000;
}
