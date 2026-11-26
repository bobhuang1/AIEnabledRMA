using System.Text.Json;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Mcp;
using AIEnabledRma.Mcp.Tools;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// The customer tools are the only MCP surface that speaks about a person. These tests pin the
/// things that stop it speaking too much: the masked projection, exact-only contact matching, a
/// floor on term length, and the session cap that refuses before it looks. The audit row and the
/// per-minute limit are asserted in <see cref="McpToolGuardTests"/>, where every tool passes
/// through them.
/// </summary>
public sealed class McpCustomerToolTests
{
    private static Customer Josephine()
    {
        var customerId = Guid.NewGuid();
        var customer = new Customer
        {
            Id = customerId,
            ExternalCustomerId = "EXT-10001",
            FirstName = "Josephine",
            LastName = "Smith",
            Email = "josephine.smith@example.com",
            PhoneNumber = "+1 510 555 0199",
            CreatedAtUtc = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero),
        };

        customer.Addresses.Add(new Address
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Label = "Home",
            Line1 = "2210 Harrison Street",
            City = "Oakland",
            PostalCode = "94612",
            CountryCode = "US",
            IsDefault = true,
        });

        return customer;
    }

    private static (CustomerLookupTools Tools, FakeCustomerRepository Customers) Build(
        int lookupsPerDay = 200,
        ICallerIdentity? caller = null)
    {
        var customers = new FakeCustomerRepository();
        var identity = caller ?? new StdioCallerIdentity();
        var budget = new CallBudget(
            new InMemoryCallMeter(),
            Options.Create(new McpBudgetOptions { CustomerLookupsPerDay = lookupsPerDay }),
            TimeProvider.System,
            identity);

        return (new CustomerLookupTools(customers, new FakeRmaRepository(), budget, identity), customers);
    }

    [Fact]
    public async Task A_customer_response_is_masked_and_carries_no_external_id()
    {
        var (tools, customers) = Build();
        customers.SearchResults.Add(Josephine());

        var response = await tools.FindCustomerAsync(
            name: "josephine smyth",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, response.MatchCount);
        var customer = Assert.Single(response.Customers);
        Assert.Equal("Josephine S.", customer.MaskedName);
        Assert.NotNull(customer.MaskedEmail);
        Assert.NotNull(customer.MaskedPhone);
        Assert.NotNull(customer.MaskedAddress);

        // The whole response, as the agent receives it: no unmasked value may appear anywhere,
        // including a property that happened to be added to the entity later.
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("josephine.smith@example.com", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("510 555 0199", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXT-10001", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Harrison", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("94612", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_partial_email_matches_nothing_even_though_the_repository_is_fuzzy()
    {
        var (tools, customers) = Build();
        customers.SearchResults.Add(Josephine());

        var response = await tools.FindCustomerAsync(
            email: "josephine.smith@exam",
            cancellationToken: TestContext.Current.CancellationToken);

        // The repository would have returned a fuzzy hit; a partial contact query is an
        // enumeration oracle, so the tool accepts only the complete value.
        Assert.Equal(0, response.MatchCount);
        Assert.Empty(response.Customers);
    }

    [Fact]
    public async Task The_complete_email_and_phone_do_match()
    {
        var (tools, customers) = Build();
        customers.SearchResults.Add(Josephine());

        var byEmail = await tools.FindCustomerAsync(
            email: "Josephine.Smith@Example.com",
            cancellationToken: TestContext.Current.CancellationToken);
        var byPhone = await tools.FindCustomerAsync(
            phoneNumber: "1 (510) 555-0199",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, byEmail.MatchCount);
        Assert.Equal(1, byPhone.MatchCount);
    }

    [Fact]
    public async Task A_term_shorter_than_the_floor_is_refused_before_the_repository_runs()
    {
        var (tools, customers) = Build();
        customers.SearchResults.Add(Josephine());

        var response = await tools.FindCustomerAsync(
            name: "jo",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, response.MatchCount);
        Assert.NotNull(response.RefusalReason);
        Assert.Equal(0, customers.SearchCalls);
    }

    [Fact]
    public async Task A_search_with_no_terms_is_refused()
    {
        var (tools, customers) = Build();

        var response = await tools.FindCustomerAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response.RefusalReason);
        Assert.Equal(0, customers.SearchCalls);
    }

    [Fact]
    public async Task A_session_that_exhausts_its_lookup_budget_is_refused_without_querying()
    {
        var (tools, customers) = Build(lookupsPerDay: 1);
        customers.SearchResults.Add(Josephine());

        var first = await tools.FindCustomerAsync(
            name: "josephine",
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await tools.FindCustomerAsync(
            name: "josephine",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, first.MatchCount);
        Assert.Equal(0, second.MatchCount);
        Assert.Contains("limit", second.RefusalReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, customers.SearchCalls);
    }

    /// <summary>
    /// D3's default half: a caller that was not granted the support scope cannot unmask, whatever it
    /// puts in the reason, and the refusal happens before the record is read.
    /// </summary>
    [Fact]
    public async Task A_caller_without_the_support_grant_is_refused_the_unmasked_record()
    {
        var (tools, customers) = Build();
        customers.ById = Josephine();

        var response = await tools.RevealCustomerContactAsync(
            customers.ById.Id,
            "customer called to correct their phone number",
            TestContext.Current.CancellationToken);

        Assert.NotNull(response.RefusalReason);
        Assert.Contains("support scope", response.RefusalReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.FullName);
        Assert.Empty(response.Addresses);

        // Refused before the lookup, so a refusal cannot be used to ask whether an id exists.
        Assert.Equal(0, customers.GetByIdCalls);

        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("josephine.smith@example.com", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("510 555 0199", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Harrison", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_support_caller_with_a_reason_gets_the_contact_record_unmasked()
    {
        var (tools, customers) = Build(caller: new SupportCallerIdentity());
        customers.ById = Josephine();

        var response = await tools.RevealCustomerContactAsync(
            customers.ById.Id,
            "customer called to correct the phone number on their return",
            TestContext.Current.CancellationToken);

        Assert.Null(response.RefusalReason);
        Assert.Equal("Josephine Smith", response.FullName);
        Assert.Equal("josephine.smith@example.com", response.Email);
        Assert.Equal("+1 510 555 0199", response.PhoneNumber);

        var address = Assert.Single(response.Addresses);
        Assert.Equal("2210 Harrison Street", address.Line1);
        Assert.Equal("94612", address.PostalCode);
        Assert.True(address.IsDefault);
    }

    /// <summary>
    /// The reason is required by the tool and kept by the guard, so a call without one — or with a
    /// keystroke — must be refused rather than audited as "unmasked, said 'x'".
    /// </summary>
    [Fact]
    public async Task A_support_caller_without_a_usable_reason_is_refused()
    {
        var (tools, customers) = Build(caller: new SupportCallerIdentity());
        customers.ById = Josephine();

        var missing = await tools.RevealCustomerContactAsync(
            customers.ById.Id,
            cancellationToken: TestContext.Current.CancellationToken);
        var keystroke = await tools.RevealCustomerContactAsync(
            customers.ById.Id,
            "x",
            TestContext.Current.CancellationToken);

        Assert.NotNull(missing.RefusalReason);
        Assert.NotNull(keystroke.RefusalReason);
        Assert.Contains("reason", missing.RefusalReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(missing.FullName);
        Assert.Null(keystroke.FullName);

        // Neither refusal read the record, so a support caller cannot use them as a lookup either.
        Assert.Equal(0, customers.GetByIdCalls);
    }

    [Fact]
    public async Task Addresses_are_masked_to_label_postcode_prefix_and_country()
    {
        var (tools, customers) = Build();
        customers.ById = Josephine();

        var response = await tools.GetCustomerAddressesAsync(
            customers.ById.Id,
            TestContext.Current.CancellationToken);

        var address = Assert.Single(response.Addresses);
        Assert.Equal("Home", address.Label);
        Assert.StartsWith("•••, 94", address.Masked);
        Assert.EndsWith("US", address.Masked);

        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("Harrison", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("94612", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_resolved_address_is_reported_masked_and_unresolved_when_below_the_threshold()
    {
        var (tools, customers) = Build();
        customers.Resolution = new AddressResolution
        {
            Address = Josephine().Addresses.First(),
            Score = 0.4,
        };

        var response = await tools.ResolveShippingAddressAsync(
            Guid.NewGuid(),
            "2210 Harrison",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(response.Resolved);
        Assert.Equal(0.4, response.MatchScore, 3);
        Assert.NotNull(response.Address);
        Assert.DoesNotContain("Harrison", response.Address.Masked, StringComparison.OrdinalIgnoreCase);
    }
}
