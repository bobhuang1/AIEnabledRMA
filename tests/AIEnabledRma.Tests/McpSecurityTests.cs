using System.Security.Claims;
using System.Text.Json;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Audit;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Mcp.Security;
using AIEnabledRma.Mcp.Tools;
using ModelContextProtocol.Protocol;

namespace AIEnabledRma.Tests;

/// <summary>
/// F1: the MCP server must hand an agent masked data only, refuse enumeration by partial contact
/// details, audit every call without storing argument values, and enforce budgets.
/// </summary>
public sealed class McpSecurityTests
{
    private static readonly Customer Jane = new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ExternalCustomerId = "EXT-SECRET-1",
        FirstName = "Jane",
        LastName = "Smith",
        Email = "jane.smith@example.com",
        PhoneNumber = "+1 (555) 010-4477",
        Addresses = [TestData.Address(line1: "2210 Harrison Street", city: "Oakland", postal: "94612")],
    };

    private static (CustomerLookupTools Tools, FakeCustomerRepository Customers) Build()
    {
        var customers = new FakeCustomerRepository();
        customers.Customers.Add(Jane);
        return (new CustomerLookupTools(customers, new FakeRmaRepository()), customers);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- projection ----------

    [Fact]
    public async Task Find_customer_returns_a_masked_projection_only()
    {
        var (tools, _) = Build();

        var response = await tools.FindCustomerAsync(email: "jane.smith@example.com", cancellationToken: Ct);

        var json = JsonSerializer.Serialize(response);
        Assert.Equal(1, response.MatchCount);
        Assert.Equal("Jane S.", response.Customers[0].MaskedName);
        Assert.Equal("j•••@e•••.com", response.Customers[0].MaskedEmail);
        Assert.Equal("••• 77", response.Customers[0].MaskedPhone);
        Assert.Equal("Oakland", response.Customers[0].DefaultAddress!.City);
        Assert.Equal("94•••", response.Customers[0].DefaultAddress!.PostalCodePrefix);

        // Nothing that identifies or locates the customer survives serialisation.
        Assert.DoesNotContain("jane.smith@example.com", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Smith", json, StringComparison.Ordinal);
        Assert.DoesNotContain("010-4477", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Harrison", json, StringComparison.Ordinal);
        Assert.DoesNotContain("94612", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EXT-SECRET-1", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Address_listing_hides_street_lines()
    {
        var (tools, _) = Build();

        var response = await tools.GetCustomerAddressesAsync(Jane.Id, Ct);

        var json = JsonSerializer.Serialize(response);
        Assert.Equal(1, response.Count);
        Assert.DoesNotContain("Harrison", json, StringComparison.Ordinal);
        Assert.DoesNotContain("94612", json, StringComparison.Ordinal);
    }

    // ---------- enumeration ----------

    [Theory]
    [InlineData("jane.smith@example", null)]
    [InlineData("jane", null)]
    [InlineData(null, "4477")]
    [InlineData(null, "555 010")]
    public async Task A_partial_email_or_phone_returns_nothing(string? email, string? phone)
    {
        var (tools, _) = Build();

        var response = await tools.FindCustomerAsync(email: email, phoneNumber: phone, cancellationToken: Ct);

        Assert.Equal(0, response.MatchCount);
        Assert.Empty(response.Customers);
    }

    [Fact]
    public async Task An_exact_phone_in_any_format_matches()
    {
        var (tools, _) = Build();

        var response = await tools.FindCustomerAsync(phoneNumber: "15550104477", cancellationToken: Ct);

        Assert.Equal(1, response.MatchCount);
    }

    [Fact]
    public async Task A_short_name_search_is_refused()
    {
        var (tools, customers) = Build();

        var response = await tools.FindCustomerAsync(name: "J ", cancellationToken: Ct);

        Assert.Equal(0, response.MatchCount);
        Assert.NotNull(response.RefusalReason);
        Assert.Empty(customers.Queries);
    }

    [Fact]
    public async Task A_name_is_not_used_to_widen_an_exact_contact_search()
    {
        var (tools, customers) = Build();

        await tools.FindCustomerAsync(name: "Jane", email: "nobody@example.com", cancellationToken: Ct);

        Assert.DoesNotContain(customers.Queries, q => q.Name is not null);
    }

    // ---------- reveal ----------

    [Fact]
    public async Task Reveal_needs_a_reason()
    {
        var (tools, _) = Build();

        var response = await tools.RevealCustomerContactAsync(Jane.Id, "because", Ct);

        Assert.NotNull(response.RefusalReason);
        Assert.Null(response.Email);
    }

    [Fact]
    public async Task A_local_caller_cannot_reveal_contact_details()
    {
        var (guard, audit) = Guard();
        var invoked = false;

        var result = await guard.RunAsync(
            Local, "s1", McpSecurityOptions.RevealTool, null,
            _ => { invoked = true; return ValueTask.FromResult(Ok(1)); },
            Ct);

        Assert.True(result.IsError);
        Assert.False(invoked);
        Assert.Equal(nameof(McpToolCallOutcome.RefusedPermission), Assert.Single(audit.Calls).Outcome);
    }

    [Fact]
    public async Task An_authenticated_support_caller_can_reveal_and_is_audited()
    {
        var (guard, audit) = Guard();
        var support = McpCaller.From(Principal(("client_id", "helpdesk"), ("roles", "rma:support")), new McpSecurityOptions());

        var result = await guard.RunAsync(
            support, "s1", McpSecurityOptions.RevealTool, null,
            _ => ValueTask.FromResult(Ok(1)), Ct);

        Assert.NotEqual(true, result.IsError);
        var row = Assert.Single(audit.Calls);
        Assert.Equal("helpdesk", row.Caller);
        Assert.Equal(nameof(McpToolCallOutcome.Ok), row.Outcome);
    }

    // ---------- audit ----------

    [Fact]
    public async Task Every_call_writes_one_audit_row_with_hashes_not_values()
    {
        var (guard, audit) = Guard();
        var args = new Dictionary<string, JsonElement>
        {
            ["email"] = JsonSerializer.SerializeToElement("jane.smith@example.com"),
        };

        await guard.RunAsync(Local, "s1", "find_customer", args, _ => ValueTask.FromResult(Ok(2)), Ct);

        var row = Assert.Single(audit.Calls);
        Assert.Equal("find_customer", row.Tool);
        Assert.Equal("local-stdio", row.Caller);
        Assert.Equal("s1", row.SessionId);
        Assert.Equal(2, row.ResultCount);
        Assert.DoesNotContain("jane", row.ArgumentHashes, StringComparison.OrdinalIgnoreCase);

        var hashes = JsonDocument.Parse(row.ArgumentHashes).RootElement;
        Assert.Equal(16, hashes.GetProperty("email").GetString()!.Length);
    }

    [Fact]
    public async Task The_same_argument_hashes_the_same_so_probing_is_visible()
    {
        var a = new Dictionary<string, JsonElement> { ["email"] = JsonSerializer.SerializeToElement("x@example.com") };
        var b = new Dictionary<string, JsonElement> { ["email"] = JsonSerializer.SerializeToElement("x@example.com") };
        var c = new Dictionary<string, JsonElement> { ["email"] = JsonSerializer.SerializeToElement("y@example.com") };

        Assert.Equal(ToolCallGuard.HashArguments(a), ToolCallGuard.HashArguments(b));
        Assert.NotEqual(ToolCallGuard.HashArguments(a), ToolCallGuard.HashArguments(c));
        await Task.CompletedTask;
    }

    [Fact]
    public void The_result_count_is_read_from_a_text_block_when_there_is_no_structured_content()
    {
        var result = new CallToolResult { Content = [new TextContentBlock { Text = "{\"matchCount\":3,\"customers\":[]}" }] };

        Assert.Equal(3, ToolCallGuard.ResultCountOf(result));
    }

    [Fact]
    public async Task A_failing_tool_is_still_audited()
    {
        var (guard, audit) = Guard();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await guard.RunAsync(
            Local, "s1", "find_device", null, _ => throw new InvalidOperationException("boom"), Ct));

        Assert.Equal(nameof(McpToolCallOutcome.Failed), Assert.Single(audit.Calls).Outcome);
    }

    // ---------- budgets ----------

    [Fact]
    public async Task The_per_minute_budget_refuses_the_next_call_and_resets_after_a_minute()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
        var (guard, audit) = Guard(new McpSecurityOptions { CallsPerMinute = 3 }, clock);

        for (var i = 0; i < 3; i++)
        {
            var ok = await guard.RunAsync(Local, "s1", "find_device", null, _ => ValueTask.FromResult(Ok(1)), Ct);
            Assert.NotEqual(true, ok.IsError);
        }

        var refused = await guard.RunAsync(Local, "s1", "find_device", null, _ => ValueTask.FromResult(Ok(1)), Ct);
        Assert.True(refused.IsError);
        Assert.Equal(nameof(McpToolCallOutcome.RefusedBudget), audit.Calls[^1].Outcome);

        clock.Now = clock.Now.AddMinutes(1);
        var again = await guard.RunAsync(Local, "s1", "find_device", null, _ => ValueTask.FromResult(Ok(1)), Ct);
        Assert.NotEqual(true, again.IsError);
    }

    [Fact]
    public async Task The_customer_lookup_budget_is_per_session()
    {
        var (guard, _) = Guard(new McpSecurityOptions { CustomerLookupsPerSession = 2 });

        await guard.RunAsync(Local, "s1", "find_customer", null, _ => ValueTask.FromResult(Ok(1)), Ct);
        await guard.RunAsync(Local, "s1", "get_customer_addresses", null, _ => ValueTask.FromResult(Ok(1)), Ct);
        var third = await guard.RunAsync(Local, "s1", "find_customer", null, _ => ValueTask.FromResult(Ok(1)), Ct);
        var device = await guard.RunAsync(Local, "s1", "find_device", null, _ => ValueTask.FromResult(Ok(1)), Ct);
        var otherSession = await guard.RunAsync(Local, "s2", "find_customer", null, _ => ValueTask.FromResult(Ok(1)), Ct);

        Assert.True(third.IsError);
        Assert.NotEqual(true, device.IsError);
        Assert.NotEqual(true, otherSession.IsError);
    }

    // ---------- caller ----------

    [Fact]
    public void An_unauthenticated_principal_is_the_local_caller_with_no_permissions()
    {
        var caller = McpCaller.From(new ClaimsPrincipal(new ClaimsIdentity()), new McpSecurityOptions());

        Assert.Equal("local-stdio", caller.Id);
        Assert.False(caller.IsAuthenticated);
        Assert.Empty(caller.Permissions);
    }

    [Fact]
    public void Scopes_are_split_and_the_client_id_wins_over_the_subject()
    {
        var caller = McpCaller.From(
            Principal(("sub", "user-7"), ("azp", "agent-app"), ("scope", "rma:read rma:support")),
            new McpSecurityOptions());

        Assert.Equal("agent-app", caller.Id);
        Assert.True(caller.Has("rma:read"));
        Assert.True(caller.Has("rma:support"));
    }

    // ---------- helpers ----------

    private static readonly McpCaller Local = McpCaller.From(null, new McpSecurityOptions());

    private static (ToolCallGuard Guard, RecordingAuditSink Audit) Guard(
        McpSecurityOptions? options = null, TimeProvider? time = null)
    {
        var audit = new RecordingAuditSink();
        var guard = new ToolCallGuard(
            audit,
            Microsoft.Extensions.Options.Options.Create(options ?? new McpSecurityOptions()),
            time ?? TimeProvider.System);
        return (guard, audit);
    }

    private static CallToolResult Ok(int count) => new()
    {
        Content = [new TextContentBlock { Text = "ok" }],
        StructuredContent = JsonSerializer.SerializeToElement(new { count }),
    };

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    internal sealed class RecordingAuditSink : IToolAuditSink
    {
        public List<McpToolCall> Calls { get; } = [];

        public Task RecordAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }

    internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
