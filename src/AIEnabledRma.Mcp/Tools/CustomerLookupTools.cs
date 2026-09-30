using System.ComponentModel;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Customers;
using ModelContextProtocol.Server;

namespace AIEnabledRma.Mcp.Tools;

/// <summary>
/// Customer lookup tools exposed over MCP.
///
/// The projection is deliberately narrower than the entity. A support agent confirming an
/// identity needs the name, the contact channels, and enough address detail for the customer
/// to recognise it. Tax id, secondary email, and internal flags are not on the path: an agent
/// that cannot see a value cannot leak it into a transcript.
/// </summary>
[McpServerToolType]
public sealed class CustomerLookupTools(ICustomerRepository customers, IRmaRepository rmas)
{
    [McpServerTool(
        Name = "find_customer",
        Title = "Find a customer by name, email, or phone")]
    [Description(
        "Find a customer by name, email address, phone number, or your own external customer "
        + "id. All fields are fuzzy-matched, so 'josephin smyth' matches 'Josephine Smith' and "
        + "a partially typed email still resolves. Returns best matches first. Confirm the "
        + "match with the customer before using any address from the result.")]
    public async Task<CustomerSearchResponse> FindCustomerAsync(
        [Description("Customer's full or partial name.")]
        string? name = null,
        [Description("Customer's email address, full or partial.")]
        string? email = null,
        [Description("Customer's phone number, with or without punctuation.")]
        string? phoneNumber = null,
        [Description("Your own CRM or ERP identifier for the customer.")]
        string? externalCustomerId = null,
        [Description("Maximum matches to return. Capped at 25.")]
        int take = 5,
        CancellationToken cancellationToken = default)
    {
        var query = new CustomerSearchQuery
        {
            Name = Normalize(name),
            Email = Normalize(email),
            PhoneNumber = Normalize(phoneNumber),
            ExternalCustomerId = Normalize(externalCustomerId),
            Take = Math.Clamp(take, 1, 25),
        };

        if (AllEmpty(query))
        {
            return new CustomerSearchResponse
            {
                MatchCount = 0,
                RefusalReason =
                    "Supply at least one of name, email, phoneNumber, or externalCustomerId. "
                    + "An unfiltered customer search is not permitted.",
                Customers = [],
            };
        }

        var matches = await customers.SearchAsync(query, cancellationToken);

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
        Name = "get_customer_addresses",
        Title = "List a customer's addresses")]
    [Description(
        "List the saved addresses for a customer so a return can be shipped to the right one. "
        + "Read-only. Use a freeTextAddress from get_customer_addresses to pick a match rather "
        + "than inventing an address.")]
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
        + "the best match with its actual match score. When nothing scores at or above "
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

    private static bool AllEmpty(CustomerSearchQuery query) =>
        string.IsNullOrWhiteSpace(query.Name)
        && string.IsNullOrWhiteSpace(query.Email)
        && string.IsNullOrWhiteSpace(query.PhoneNumber)
        && string.IsNullOrWhiteSpace(query.ExternalCustomerId);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CustomerSummary ToSummary(Customer customer) => new()
    {
        Id = customer.Id,
        FullName = customer.FullName,
        CompanyName = customer.CompanyName,
        Email = customer.Email,
        PhoneNumber = customer.PhoneNumber,
        PreferredLanguage = customer.PreferredLanguage,
        ExternalCustomerId = customer.ExternalCustomerId,
        DefaultAddress = customer.Addresses
            .Where(a => a.IsDefault)
            .Select(ToAddressSummary)
            .FirstOrDefault()
            ?? customer.Addresses
                .OrderBy(a => a.Line1, StringComparer.OrdinalIgnoreCase)
                .Select(ToAddressSummary)
                .FirstOrDefault(),
        AddressCount = customer.Addresses.Count,
    };

    private static AddressSummary ToAddressSummary(Address address) => new()
    {
        Id = address.Id,
        Label = address.Label,
        Line1 = address.Line1,
        Line2 = address.Line2,
        Line3 = address.Line3,
        City = address.City,
        StateOrProvince = address.StateOrProvince,
        PostalCode = address.PostalCode,
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

public sealed record CustomerSummary
{
    public required Guid Id { get; init; }

    public required string FullName { get; init; }

    public string? CompanyName { get; init; }

    public required string Email { get; init; }

    public required string PhoneNumber { get; init; }

    public string? PreferredLanguage { get; init; }

    public string? ExternalCustomerId { get; init; }

    public AddressSummary? DefaultAddress { get; init; }

    public required int AddressCount { get; init; }
}

public sealed record AddressListResponse
{
    public required Guid CustomerId { get; init; }

    public required int Count { get; init; }

    public required IReadOnlyList<AddressSummary> Addresses { get; init; }

    public bool NotFound { get; init; }
}

public sealed record AddressSummary
{
    public required Guid Id { get; init; }

    public string? Label { get; init; }

    public required string Line1 { get; init; }

    public string? Line2 { get; init; }

    public string? Line3 { get; init; }

    public required string City { get; init; }

    public string? StateOrProvince { get; init; }

    public required string PostalCode { get; init; }

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
