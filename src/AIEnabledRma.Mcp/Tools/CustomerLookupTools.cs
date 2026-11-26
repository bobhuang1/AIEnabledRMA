using System.ComponentModel;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Customers;
using ModelContextProtocol.Server;

namespace AIEnabledRma.Mcp.Tools;

/// <summary>
/// Customer lookup tools exposed over MCP.
/// </summary>
/// <remarks>
/// Everything here goes through <see cref="CustomerProjection"/>, the same masking the customer
/// wizard uses. These tools used to return the entity's fields — full name, email, phone, external
/// id, street address — which put a customer's contact details into whatever model transcript the
/// agent was talking to. There is no caller identity on stdio, so the projection is the control
/// that holds whatever the session is.
///
/// Name searches stay fuzzy, because a name is not an identifier: "josephin smyth" is a person
/// typing their own name badly. Email and phone must match exactly. A partial contact lookup is an
/// enumeration oracle — the match count answers "does this address exist?" one character at a time
/// — and masking the output does not help when the question is the count.
///
/// Audit rows and the per-minute budget are not here: they belong to
/// <see cref="ToolCallGuard"/>, which every tool passes through. What stays here is the cap on
/// customer lookups per session, because that one is about what a call is for.
///
/// One tool breaks the masking, and only for a caller the identity provider says may: see
/// <see cref="RevealCustomerContactAsync"/>. It is here rather than beside the others in a separate
/// class because it is the same question — which customer is this — answered under a stricter grant,
/// and a second class would be a second place for the masking rule to be forgotten.
/// </remarks>
[McpServerToolType]
public sealed class CustomerLookupTools(
    ICustomerRepository customers,
    IRmaRepository rmas,
    CallBudget budget,
    ICallerIdentity caller)
{
    /// <summary>
    /// Shortest term these tools accept. Below this, a term is a prefix of many records, which is
    /// exactly the shape of a scan.
    /// </summary>
    private const int MinimumQueryLength = 3;

    /// <summary>
    /// Shortest justification a reveal is accepted with. A reason shorter than a sentence is a
    /// keystroke, and the audit row would say "unmasked, said 'x'" — which is a record of the act
    /// with none of its justification.
    /// </summary>
    private const int MinimumReasonLength = 10;

    [McpServerTool(
        Name = "find_customer",
        Title = "Find a masked customer by name, or by exact email or phone")]
    [Description(
        "Find a customer by full or partial name, or confirm one by their complete email address "
        + "or phone number. Names are fuzzy-matched; email and phone must be complete and match "
        + "exactly, because a partial contact search is an enumeration oracle. Every field in the "
        + "response is masked. Confirm the match with the customer before using any address from "
        + "the result.")]
    public async Task<CustomerSearchResponse> FindCustomerAsync(
        [Description("Customer's full or partial name.")]
        string? name = null,
        [Description("The customer's complete email address. Partial values match nothing.")]
        string? email = null,
        [Description("The customer's complete phone number, with or without punctuation.")]
        string? phoneNumber = null,
        [Description("Maximum matches to return. Capped at 25.")]
        int take = 5,
        CancellationToken cancellationToken = default)
    {
        var nameQuery = Normalize(name);
        var emailQuery = Normalize(email);
        var phoneQuery = Normalize(phoneNumber);

        if (nameQuery is null && emailQuery is null && phoneQuery is null)
        {
            return RefusedSearch(
                "Supply at least one of name, email, or phoneNumber. An unfiltered customer "
                + "search is not permitted.");
        }

        if (Shortest(nameQuery, emailQuery, phoneQuery) is { } tooShort)
        {
            return RefusedSearch(
                $"Search terms must be at least {MinimumQueryLength} characters, and '{tooShort}' "
                + "is shorter. A shorter term matches a prefix of the customer table.");
        }

        var budgetDecision = await budget.TryConsumeCustomerLookupAsync(cancellationToken);

        if (!budgetDecision.Allowed)
        {
            return RefusedSearch(budgetDecision.Refusal!);
        }

        var matches = (await customers.SearchAsync(
                new CustomerSearchQuery
                {
                    Name = nameQuery,
                    Email = emailQuery,
                    PhoneNumber = phoneQuery,
                    Take = Math.Clamp(take, 1, 25),
                },
                cancellationToken))
            // The repository is fuzzy on every field; the caller is not allowed to be.
            .Where(c => CustomerProjection.MatchesExactContact(c, emailQuery, phoneQuery))
            .ToList();

        return new CustomerSearchResponse
        {
            MatchCount = matches.Count,
            // Ambiguity is the dangerous case in a returns flow, not a low match count:
            // two customers on the same account is normal. The caller is told to
            // disambiguate.
            RequiresConfirmation = matches.Count > 1,
            Customers = matches.Select(CustomerProjection.From).ToList(),
        };
    }

    /// <summary>
    /// The one tool that returns unmasked contact data. D3: masked by default, a full record only
    /// for an authenticated support role, audited.
    /// </summary>
    /// <remarks>
    /// The grant is the token's support scope, checked through <see cref="ICallerIdentity"/> so the
    /// decision and the audit row are about the same caller. The reason is required because the
    /// row keeps it: the guard hashes arguments, and this is the one value it stores as written.
    /// stdio has no token, so nobody on stdio can call this — the local agent's way to see a
    /// customer is the masked tools, which is the same answer the wizard gives.
    ///
    /// Refused before the budget is spent and before the record is read, so a caller without the
    /// grant cannot use the refusals to learn whether a customer id exists.
    /// </remarks>
    [McpServerTool(
        Name = "reveal_customer_contact",
        Title = "Reveal a customer's full contact record (authenticated support role only)")]
    [Description(
        "Return a customer's unmasked name, email address, phone number and saved addresses, so "
        + "support can reach the customer. Refused unless the caller's token carries the support "
        + "scope, and it requires a reason, which is recorded in the audit trail. Use it when the "
        + "customer is on the line and the masked view is not enough; do not quote the result into "
        + "a transcript that outlives the call.")]
    public async Task<ContactRevealResponse> RevealCustomerContactAsync(
        [Description("The customer id, as returned by find_customer.")]
        Guid customerId,
        [Description(
            "Why this record is being unmasked, in a sentence, e.g. 'customer called to correct "
            + "their phone number'. It is stored as written in mcp_tool_calls.")]
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        // The grant first, before anything that would tell a caller something: a refusal here is
        // the same shape whether or not the id exists.
        if (!caller.IsSupportCaller)
        {
            return RefusedReveal(
                customerId,
                "Revealing a customer's contact details needs a token carrying the support scope. "
                + "This caller has no such grant, so the masked tools are everything it may read.");
        }

        var justification = reason?.Trim();

        if (justification is null || justification.Length < MinimumReasonLength)
        {
            return RefusedReveal(
                customerId,
                $"A reason of at least {MinimumReasonLength} characters is required to reveal a "
                + "contact record, and it is recorded in the audit trail. Say what the customer "
                + "asked for and why the masked view is not enough.");
        }

        var budgetDecision = await budget.TryConsumeCustomerLookupAsync(cancellationToken);

        if (!budgetDecision.Allowed)
        {
            return RefusedReveal(customerId, budgetDecision.Refusal!);
        }

        var customer = await customers.GetByIdAsync(customerId, cancellationToken);

        if (customer is null)
        {
            return new ContactRevealResponse
            {
                CustomerId = customerId,
                NotFound = true,
                Addresses = [],
            };
        }

        return new ContactRevealResponse
        {
            CustomerId = customerId,
            FullName = customer.FullName,
            CompanyName = customer.CompanyName,
            Email = customer.Email,
            PhoneNumber = customer.PhoneNumber,
            Addresses = customer.Addresses
                .OrderByDescending(a => a.IsDefault)
                .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
                .Select(ToRevealedAddress)
                .ToList(),
        };
    }

    [McpServerTool(
        Name = "get_customer_addresses",
        Title = "List a customer's masked addresses")]
    [Description(
        "List the saved addresses for a customer so a return can be shipped to the right one. Each "
        + "address is masked to its label, a partial postcode and the country: enough for the "
        + "customer to recognise by position, not enough to learn it. Read-only.")]
    public async Task<AddressListResponse> GetCustomerAddressesAsync(
        [Description("The customer id, as returned by find_customer.")]
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        var budgetDecision = await budget.TryConsumeCustomerLookupAsync(cancellationToken);

        if (!budgetDecision.Allowed)
        {
            return RefusedAddresses(customerId, budgetDecision.Refusal!);
        }

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

        var addresses = customer.Addresses
            .OrderByDescending(a => a.IsDefault)
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .Select(ToMaskedAddress)
            .ToList();

        return new AddressListResponse
        {
            CustomerId = customerId,
            Count = addresses.Count,
            Addresses = addresses,
        };
    }

    [McpServerTool(
        Name = "get_customer_returns",
        Title = "List a customer's returns")]
    [Description(
        "List every return on a customer's record, newest first, so an agent can see the "
        + "history before creating another. Read-only. Contains no contact details.")]
    public async Task<CustomerReturnsResponse> GetCustomerReturnsAsync(
        [Description("The customer id, as returned by find_customer.")]
        Guid customerId,
        [Description("Maximum returns to return. Capped at 50.")]
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var budgetDecision = await budget.TryConsumeCustomerLookupAsync(cancellationToken);

        if (!budgetDecision.Allowed)
        {
            return RefusedReturns(customerId, budgetDecision.Refusal!);
        }

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
        "Match a free-text address the customer typed against their saved addresses. Returns the "
        + "best match with its actual match score, masked to label, partial postcode and country. "
        + "When nothing scores at or above minimumScore, the customer's default address is returned "
        + "with resolved=false and isFallback=true: ask the customer to confirm the address rather "
        + "than assuming they meant their default.")]
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

        var budgetDecision = await budget.TryConsumeCustomerLookupAsync(cancellationToken);

        if (!budgetDecision.Allowed)
        {
            return RefusedResolution(customerId, budgetDecision.Refusal!);
        }

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
        // threshold here would report a weak match as a perfect one.
        var accepted = !resolution.IsFallback && resolution.Score >= minimumScore;

        return new AddressResolutionResponse
        {
            CustomerId = customerId,
            Resolved = accepted,
            MatchScore = resolution.Score,
            IsFallback = resolution.IsFallback,
            Address = ToMaskedAddress(resolution.Address),
            Reason = accepted
                ? null
                : "Nothing scored at or above the minimum, so the default address is shown "
                  + "as a suggestion only. Confirm it with the customer before using it.",
        };
    }

    /// <summary>
    /// The address as it is stored, for a caller with the support grant. The masked projection above
    /// stays the shape everything else sees.
    /// </summary>
    private static RevealedAddress ToRevealedAddress(Address address) => new()
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

    private static ContactRevealResponse RefusedReveal(Guid customerId, string reason) =>
        new()
        {
            CustomerId = customerId,
            RefusalReason = reason,
            Addresses = [],
        };

    private static MaskedAddressSummary ToMaskedAddress(Address address) => new()
    {
        Id = address.Id,
        Label = address.Label,
        Masked = CustomerProjection.MaskAddress(address),
        IsDefault = address.IsDefault,
    };

    private static CustomerSearchResponse RefusedSearch(string reason) =>
        new() { MatchCount = 0, RefusalReason = reason, Customers = [] };

    private static AddressListResponse RefusedAddresses(Guid customerId, string reason) =>
        new() { CustomerId = customerId, Count = 0, Addresses = [], RefusalReason = reason };

    private static CustomerReturnsResponse RefusedReturns(Guid customerId, string reason) =>
        new()
        {
            CustomerId = customerId,
            Count = 0,
            TotalCount = 0,
            RefusalReason = reason,
            Returns = [],
        };

    private static AddressResolutionResponse RefusedResolution(Guid customerId, string reason) =>
        new()
        {
            CustomerId = customerId,
            Resolved = false,
            RefusalReason = reason,
        };

    private static string? Shortest(params string?[] values) =>
        values.FirstOrDefault(v => v is { Length: < MinimumQueryLength });

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    public required IReadOnlyList<CustomerProjection> Customers { get; init; }
}

/// <summary>
/// A customer's contact record unmasked: the answer <c>reveal_customer_contact</c> gives a support
/// caller. Deliberately not the entity and not a superset of the masked projection — the tax id,
/// the alternate email and the marketing opt-in are not what a return needs, and "unmask the
/// contact" should not quietly become "unmask the record".
/// </summary>
public sealed record ContactRevealResponse
{
    public required Guid CustomerId { get; init; }

    /// <summary>True when no customer has that id. Still a refusal-free answer, because the
    /// caller was allowed to ask.</summary>
    public bool NotFound { get; init; }

    public string? FullName { get; init; }

    public string? CompanyName { get; init; }

    public string? Email { get; init; }

    public string? PhoneNumber { get; init; }

    public required IReadOnlyList<RevealedAddress> Addresses { get; init; }

    /// <summary>Set when the call was refused rather than answered.</summary>
    public string? RefusalReason { get; init; }
}

/// <summary>One stored address, unreduced.</summary>
public sealed record RevealedAddress
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

public sealed record AddressListResponse
{
    public required Guid CustomerId { get; init; }

    public required int Count { get; init; }

    public required IReadOnlyList<MaskedAddressSummary> Addresses { get; init; }

    public bool NotFound { get; init; }

    public string? RefusalReason { get; init; }
}

public sealed record MaskedAddressSummary
{
    public required Guid Id { get; init; }

    public string? Label { get; init; }

    public required string Masked { get; init; }

    public required bool IsDefault { get; init; }
}

public sealed record AddressResolutionResponse
{
    public required Guid CustomerId { get; init; }

    public required bool Resolved { get; init; }

    public string? Reason { get; init; }

    /// <summary>Set when the call was refused rather than answered.</summary>
    public string? RefusalReason { get; init; }

    /// <summary>The score the matcher actually achieved, not the caller's threshold.</summary>
    public double MatchScore { get; init; }

    /// <summary>True when the default address was substituted because nothing matched.</summary>
    public bool IsFallback { get; init; }

    public MaskedAddressSummary? Address { get; init; }
}

public sealed record CustomerReturnsResponse
{
    public required Guid CustomerId { get; init; }

    public required int Count { get; init; }

    public required int TotalCount { get; init; }

    public string? RefusalReason { get; init; }

    public required IReadOnlyList<CustomerReturnSummary> Returns { get; init; }
}

public sealed record CustomerReturnSummary
{
    public required Guid Id { get; init; }

    public required string RmaNumber { get; init; }

    public required string Status { get; init; }

    public required string Kind { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public int LineCount { get; init; }
}
