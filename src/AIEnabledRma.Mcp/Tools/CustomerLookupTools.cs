using System.ComponentModel;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Customers;
using ModelContextProtocol.Server;

namespace AIEnabledRma.Mcp.Tools;

/// <summary>
/// Customer lookup tools exposed over MCP.
///
/// Every result is masked (see <see cref="CustomerMasking"/>): an agent sees "Jane S.", a masked
/// email and phone, and a city with a postal-code prefix, never a street line, a full contact
/// detail or the external CRM id. Contact search is exact-only, so a partial email or phone
/// cannot be used to enumerate customers; only a name search is fuzzy, and its output is masked.
/// Full contact details are available only through <c>reveal_customer_contact</c>, which the
/// tool-call guard restricts to an authenticated support role and audits.
/// </summary>
[McpServerToolType]
public sealed class CustomerLookupTools(ICustomerRepository customers, IRmaRepository rmas)
{
    /// <summary>Shortest name search accepted, in non-space characters.</summary>
    public const int MinimumNameLength = 3;

    /// <summary>Shortest justification accepted by <c>reveal_customer_contact</c>.</summary>
    public const int MinimumRevealReasonLength = 10;

    [McpServerTool(
        Name = "find_customer",
        Title = "Find a customer by name, email, or phone")]
    [Description(
        "Find a customer. Email and phone must be the customer's exact contact detail (no partial "
        + "matches); a name (at least 3 characters) is fuzzy-matched. Results are masked: confirm "
        + "the match with the customer, who knows their own details, before acting on it.")]
    public async Task<CustomerSearchResponse> FindCustomerAsync(
        [Description("Customer's name, at least 3 characters. Fuzzy-matched.")]
        string? name = null,
        [Description("Customer's exact email address.")]
        string? email = null,
        [Description("Customer's full phone number, with or without punctuation.")]
        string? phoneNumber = null,
        [Description("Your own CRM or ERP identifier for the customer. Exact match only.")]
        string? externalCustomerId = null,
        [Description("Maximum matches to return. Capped at 10.")]
        int take = 5,
        CancellationToken cancellationToken = default)
    {
        name = Normalize(name);
        email = Normalize(email);
        phoneNumber = Normalize(phoneNumber);
        externalCustomerId = Normalize(externalCustomerId);
        take = Math.Clamp(take, 1, 10);

        if (name is null && email is null && phoneNumber is null && externalCustomerId is null)
        {
            return Refused(
                "Supply at least one of name, email, phoneNumber, or externalCustomerId. "
                + "An unfiltered customer search is not permitted.");
        }

        if (name is not null && name.Count(c => !char.IsWhiteSpace(c)) < MinimumNameLength)
        {
            return Refused($"A name search needs at least {MinimumNameLength} characters.");
        }

        var found = new Dictionary<Guid, Customer>();

        void Add(IEnumerable<Customer> hits)
        {
            foreach (var hit in hits)
            {
                found.TryAdd(hit.Id, hit);
            }
        }

        if (externalCustomerId is not null)
        {
            Add(await customers.SearchAsync(
                new CustomerSearchQuery { ExternalCustomerId = externalCustomerId, Take = take },
                cancellationToken));
        }

        // Contact details are matched exactly. The repository's search is fuzzy, so its hits are
        // only candidates; anything that is not the exact email or the same phone digits is
        // dropped, so a partial value returns nothing rather than a list of near neighbours.
        if (email is not null)
        {
            var hits = await customers.SearchAsync(
                new CustomerSearchQuery { Email = email, Take = 25 }, cancellationToken);
            Add(hits.Where(c => CustomerMasking.IsExactEmailMatch(email, c)));
        }

        if (phoneNumber is not null)
        {
            var hits = await customers.SearchAsync(
                new CustomerSearchQuery { PhoneNumber = phoneNumber, Take = 25 }, cancellationToken);
            Add(hits.Where(c => CustomerMasking.IsExactPhoneMatch(phoneNumber, c)));
        }

        // A name is only searched on its own. Combined with an exact contact detail it would
        // widen an exact lookup back into a fuzzy one.
        if (name is not null && email is null && phoneNumber is null && externalCustomerId is null)
        {
            Add(await customers.SearchAsync(
                new CustomerSearchQuery { Name = name, Take = take }, cancellationToken));
        }

        var matches = found.Values.Take(take).ToList();

        return new CustomerSearchResponse
        {
            MatchCount = matches.Count,
            // Ambiguity is the dangerous case in a returns flow, not a low match count: two
            // customers on the same account is normal. The caller is told to disambiguate.
            RequiresConfirmation = matches.Count > 1,
            Customers = matches.Select(ToSummary).ToList(),
        };
    }

    [McpServerTool(
        Name = "reveal_customer_contact",
        Title = "Reveal a customer's full contact details (support role only)")]
    [Description(
        "Return a customer's full name, email and phone. Only an authenticated caller with the "
        + "support role may use this; every call is audited. Use it only when you must contact "
        + "the customer and they cannot give you the details themselves.")]
    public async Task<CustomerContactResponse> RevealCustomerContactAsync(
        [Description("The customer id, as returned by find_customer.")]
        Guid customerId,
        [Description("Why the full details are needed, at least 10 characters.")]
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < MinimumRevealReasonLength)
        {
            return new CustomerContactResponse
            {
                CustomerId = customerId,
                RefusalReason = $"Give a reason of at least {MinimumRevealReasonLength} characters.",
            };
        }

        var customer = await customers.GetByIdAsync(customerId, cancellationToken);
        if (customer is null)
        {
            return new CustomerContactResponse { CustomerId = customerId, NotFound = true };
        }

        return new CustomerContactResponse
        {
            CustomerId = customerId,
            FullName = customer.FullName,
            Email = customer.Email,
            PhoneNumber = customer.PhoneNumber,
        };
    }

    [McpServerTool(
        Name = "get_customer_addresses",
        Title = "List a customer's addresses")]
    [Description(
        "List the saved addresses for a customer (masked: city and postal-code prefix) so the "
        + "customer can say which one to ship to. Read-only. Refer to an address by its id.")]
    public async Task<AddressListResponse> GetCustomerAddressesAsync(
        [Description("The customer id, as returned by find_customer.")]
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        var customer = await customers.GetByIdAsync(customerId, cancellationToken);

        if (customer is null)
        {
            return new AddressListResponse
            {
                CustomerId = customerId,
                Count = 0,
                Addresses = [],
                NotFound = true,
            };
        }

        return new AddressListResponse
        {
            CustomerId = customerId,
            Count = customer.Addresses.Count,
            Addresses = customer.Addresses
                .OrderByDescending(a => a.IsDefault)
                .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
                .Select(ToAddressSummary)
                .ToList(),
        };
    }

    [McpServerTool(
        Name = "get_customer_returns",
        Title = "List a customer's returns")]
    [Description(
        "List every return on a customer's record, newest first, so an agent can see the "
        + "history before creating another. Read-only.")]
    public async Task<CustomerReturnsResponse> GetCustomerReturnsAsync(
        [Description("The customer id, as returned by find_customer.")]
        Guid customerId,
        [Description("Maximum returns to return. Capped at 50.")]
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var all = await rmas.GetForCustomerAsync(customerId, cancellationToken);

        var page = all
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 50))
            .ToList();

        return new CustomerReturnsResponse
        {
            CustomerId = customerId,
            Count = page.Count,
            TotalCount = all.Count,
            Returns = page.Select(r => new CustomerReturnSummary
            {
                Id = r.Id,
                RmaNumber = r.RmaNumber,
                Status = r.Status.ToString(),
                Kind = r.Kind.ToString(),
                CreatedAtUtc = r.CreatedAtUtc,
                LineCount = r.Lines.Count,
            }).ToList(),
        };
    }

    [McpServerTool(
        Name = "resolve_shipping_address",
        Title = "Resolve a typed address against saved addresses")]
    [Description(
        "Match a free-text address the customer typed against their saved addresses. Returns "
        + "the best match (masked) with its actual match score. When nothing scores at or above "
        + "minimumScore, the customer's default address is returned with resolved=false and "
        + "isFallback=true: ask the customer to confirm the address rather than assuming "
        + "they meant their default.")]
    public async Task<AddressResolutionResponse> ResolveShippingAddressAsync(
        [Description("The customer id, as returned by find_customer.")]
        Guid customerId,
        [Description("The address the customer typed, in any format.")]
        string freeTextAddress,
        [Description("Minimum match score to accept. Defaults to 0.55.")]
        double minimumScore = 0.55,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(freeTextAddress);

        var resolution = await customers.ResolveAddressAsync(
            customerId,
            freeTextAddress,
            cancellationToken);

        if (resolution is null)
        {
            return new AddressResolutionResponse
            {
                CustomerId = customerId,
                Resolved = false,
                Reason =
                    "This customer has no saved address. Collect the full address and save it "
                    + "before continuing, rather than shipping to a guess.",
            };
        }

        // Report the score the matcher actually achieved. Returning the caller's own
        // threshold here would report a weak match as a perfect one, and an agent would
        // read that as the address being confidently identified.
        var accepted = !resolution.IsFallback && resolution.Score >= minimumScore;

        return new AddressResolutionResponse
        {
            CustomerId = customerId,
            Resolved = accepted,
            MatchScore = resolution.Score,
            IsFallback = resolution.IsFallback,
            Address = ToAddressSummary(resolution.Address),
            Reason = accepted
                ? null
                : "Nothing scored at or above the minimum, so the default address is shown "
                  + "as a suggestion only. Confirm it with the customer before using it.",
        };
    }

    private static CustomerSearchResponse Refused(string reason) => new()
    {
        MatchCount = 0,
        RefusalReason = reason,
        Customers = [],
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static CustomerSummary ToSummary(Customer customer) => new()
    {
        Id = customer.Id,
        MaskedName = CustomerMasking.MaskName(customer.FirstName, customer.LastName),
        CompanyName = customer.CompanyName,
        MaskedEmail = CustomerMasking.MaskEmail(customer.Email),
        MaskedPhone = CustomerMasking.MaskPhone(customer.PhoneNumber),
        PreferredLanguage = customer.PreferredLanguage,
        DefaultAddress = customer.Addresses
            .Where(a => a.IsDefault)
            .Select(ToAddressSummary)
            .FirstOrDefault()
            ?? customer.Addresses
                .OrderBy(a => a.Id)
                .Select(ToAddressSummary)
                .FirstOrDefault(),
        AddressCount = customer.Addresses.Count,
    };

    /// <summary>
    /// Street lines and the full postal code stay on the server. The city and a postal-code
    /// prefix are enough for the customer to recognise which saved address is meant, and the
    /// address id is what the rest of the flow uses.
    /// </summary>
    internal static AddressSummary ToAddressSummary(Address address) => new()
    {
        Id = address.Id,
        Label = address.Label,
        City = address.City,
        StateOrProvince = address.StateOrProvince,
        PostalCodePrefix = CustomerMasking.MaskPostalCode(address.PostalCode),
        CountryCode = address.CountryCode,
        IsDefault = address.IsDefault,
    };
}

public sealed record CustomerSearchResponse
{
    public required int MatchCount { get; init; }

    /// <summary>
    /// True when more than one customer matched. The agent must confirm which one it means
    /// with the customer before acting.
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    public string? RefusalReason { get; init; }

    public required IReadOnlyList<CustomerSummary> Customers { get; init; }
}

/// <summary>A masked customer. No full contact detail and no external CRM id.</summary>
public sealed record CustomerSummary
{
    public required Guid Id { get; init; }

    /// <summary>First name and last-name initial.</summary>
    public required string MaskedName { get; init; }

    public string? CompanyName { get; init; }

    public string? MaskedEmail { get; init; }

    public string? MaskedPhone { get; init; }

    public string? PreferredLanguage { get; init; }

    public AddressSummary? DefaultAddress { get; init; }

    public required int AddressCount { get; init; }
}

public sealed record CustomerContactResponse
{
    public required Guid CustomerId { get; init; }

    public string? FullName { get; init; }

    public string? Email { get; init; }

    public string? PhoneNumber { get; init; }

    public bool NotFound { get; init; }

    public string? RefusalReason { get; init; }
}

public sealed record AddressListResponse
{
    public required Guid CustomerId { get; init; }

    public required int Count { get; init; }

    public required IReadOnlyList<AddressSummary> Addresses { get; init; }

    public bool NotFound { get; init; }
}

/// <summary>A masked address: city, region, postal-code prefix and country only.</summary>
public sealed record AddressSummary
{
    public required Guid Id { get; init; }

    public string? Label { get; init; }

    public required string City { get; init; }

    public string? StateOrProvince { get; init; }

    /// <summary>The first two characters of the postal code, the rest masked.</summary>
    public required string PostalCodePrefix { get; init; }

    public required string CountryCode { get; init; }

    public required bool IsDefault { get; init; }
}

public sealed record AddressResolutionResponse
{
    public required Guid CustomerId { get; init; }

    public required bool Resolved { get; init; }

    public string? Reason { get; init; }

    /// <summary>The score the matcher actually achieved, not the caller's threshold.</summary>
    public double MatchScore { get; init; }

    /// <summary>True when the default address was substituted because nothing matched.</summary>
    public bool IsFallback { get; init; }

    public AddressSummary? Address { get; init; }
}

public sealed record CustomerReturnsResponse
{
    public required Guid CustomerId { get; init; }

    public required int Count { get; init; }

    public required int TotalCount { get; init; }

    public required IReadOnlyList<CustomerReturnSummary> Returns { get; init; }
}

public sealed record CustomerReturnSummary
{
    public required Guid Id { get; init; }

    public required string RmaNumber { get; init; }

    public required string Status { get; init; }

    public required string Kind { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required int LineCount { get; init; }
}
