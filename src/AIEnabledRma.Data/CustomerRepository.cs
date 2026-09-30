using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Data;

public sealed class CustomerRepository(
    RmaDbContext db,
    IOptions<CustomerLookupOptions> options)
    : ICustomerRepository
{
    private readonly RmaDbContext _db = db;
    private readonly CustomerLookupOptions _options = options.Value;

    public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Customers
            .Include(c => c.Addresses)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <summary>
    /// Two-stage lookup. The database does trigram prefiltering and returns a bounded
    /// candidate set; <see cref="FuzzyMatcher"/> then re-ranks in memory. Doing it this way
    /// keeps the index doing the expensive narrowing while keeping the ranking rules in C#,
    /// where they are unit-testable and identical to the in-process path.
    /// </summary>
    public async Task<IReadOnlyList<Customer>> SearchAsync(
        CustomerSearchQuery query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Name)
            && string.IsNullOrWhiteSpace(query.Email)
            && string.IsNullOrWhiteSpace(query.PhoneNumber)
            && string.IsNullOrWhiteSpace(query.ExternalCustomerId))
        {
            return [];
        }

        var q = _db.Customers.Include(c => c.Addresses).AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.ExternalCustomerId))
        {
            var external = query.ExternalCustomerId.Trim();
            return await q
                .Where(c => c.ExternalCustomerId == external)
                // Ordered before Take so the same query returns the same page. An unordered
                // Take is a row-limiting plan with no defined order, so which rows come back
                // can change with the plan chosen and the physical row order.
                .OrderBy(c => c.Id)
                .Take(query.Take)
                .ToListAsync(cancellationToken);
        }

        // Build a server-side prefilter from whatever the caller supplied. Each clause is
        // trigram-indexed; the OR keeps a partial match on any one field sufficient.
        var name = query.Name?.Trim();
        var email = query.Email?.Trim();
        var phone = query.PhoneNumber?.Trim();

        var prefiltered = q.Where(c =>
            (name != null && EF.Functions.ILike(
                c.FirstName + " " + c.LastName, $"%{name}%"))
            || (email != null && EF.Functions.ILike(c.Email, $"%{email}%"))
            || (phone != null && EF.Functions.ILike(c.PhoneNumber, $"%{phone}%")));

        // Ordered so the candidate pool is a stable subset. Without an order the trigram
        // prefilter's Take returns an arbitrary slice, and the same customer could match one
        // query and not the next.
        var candidates = await prefiltered
            .OrderBy(c => c.Id)
            .Take(_options.CandidatePoolSize)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0 && _options.AllowFullScanFallback)
        {
            var total = await _db.Customers.CountAsync(cancellationToken);
            if (total <= _options.FullScanThreshold)
            {
                candidates = await q
                    .OrderBy(c => c.Id)
                    .Take(_options.FullScanThreshold)
                    .ToListAsync(cancellationToken);
            }
        }

        var scored = candidates
            .Select(c => (Customer: c, Score: ScoreCustomer(query, c)))
            .Where(x => x.Score >= _options.MinimumMatchScore)
            .OrderByDescending(x => x.Score)
            .ThenBy(c => c.Customer.LastName, StringComparer.OrdinalIgnoreCase)
            // Last name is not a unique key, so the ranking would still be able to drop and
            // pick rows arbitrarily at the Take boundary without a final tiebreak.
            .ThenBy(c => c.Customer.Id)
            .Take(query.Take)
            .ToList();

        return scored.Select(x => x.Customer).ToList();
    }

    public async Task<AddressResolution?> ResolveAddressAsync(
        Guid customerId,
        string? freeTextAddress,
        CancellationToken cancellationToken)
    {
        var addresses = await _db.Addresses
            .Where(a => a.CustomerId == customerId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (addresses.Count == 0)
        {
            return null;
        }

        var defaultAddress = addresses.FirstOrDefault(a => a.IsDefault) ?? addresses[0];

        if (string.IsNullOrWhiteSpace(freeTextAddress))
        {
            return new AddressResolution { Address = defaultAddress, Score = 1d, IsDefault = true };
        }

        // Compare on the concatenated single-line form, because a customer reading an
        // address off a card rarely reproduces the field breaks.
        var scored = addresses
            .Select(a => (Address: a, Score: FuzzyMatcher.Score(freeTextAddress, Flatten(a))))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Address.IsDefault ? 1 : 0)
            .ToList();

        if (scored[0].Score < _options.MinimumMatchScore)
        {
            // No confident match. The customer's own default is the safer choice, and the
            // wizard shows the address for confirmation before the request is confirmed.
            // The substitution is reported rather than hidden, so the caller can ask the
            // customer to confirm instead of assuming they meant their default.
            return new AddressResolution
            {
                Address = defaultAddress,
                Score = scored[0].Score,
                IsFallback = true,
            };
        }

        return new AddressResolution
        {
            Address = scored[0].Address,
            Score = scored[0].Score,
            IsDefault = scored[0].Address.IsDefault,
        };
    }

    private static string Flatten(Address a) =>
        string.Join(
            " ",
            new[] { a.Line1, a.Line2, a.Line3, a.City, a.StateOrProvince, a.PostalCode, a.CountryCode }
                .Where(p => !string.IsNullOrWhiteSpace(p)));

    private static double ScoreCustomer(CustomerSearchQuery query, Customer c)
    {
        var scores = new List<double>();

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            scores.Add(FuzzyMatcher.BestScore(query.Name, c.FirstName, c.LastName, c.FullName));
        }

        if (!string.IsNullOrWhiteSpace(query.Email))
        {
            // An exact email match is a strong signal, so it is given extra weight.
            var emailScore = FuzzyMatcher.Score(query.Email, c.Email);
            scores.Add(string.Equals(query.Email.Trim(), c.Email, StringComparison.OrdinalIgnoreCase)
                ? 1d
                : emailScore);
        }

        if (!string.IsNullOrWhiteSpace(query.PhoneNumber))
        {
            var digitsOnly = new string(query.PhoneNumber.Where(char.IsAsciiDigit).ToArray());
            var candidateDigits = new string(c.PhoneNumber.Where(char.IsAsciiDigit).ToArray());

            scores.Add(digitsOnly.Length >= 7 && digitsOnly.EndsWith(
                candidateDigits.Length >= 7
                    ? candidateDigits[^Math.Min(7, candidateDigits.Length)..]
                    : candidateDigits,
                StringComparison.Ordinal)
                ? 1d
                : FuzzyMatcher.Score(digitsOnly, candidateDigits));
        }

        return scores.Count == 0 ? 0d : scores.Max();
    }
}

public sealed class CustomerLookupOptions
{
    public const string SectionName = "CustomerLookup";

    public double MinimumMatchScore { get; set; } = 0.55;

    public int CandidatePoolSize { get; set; } = 50;

    public bool AllowFullScanFallback { get; set; } = true;

    public int FullScanThreshold { get; set; } = 5000;
}
