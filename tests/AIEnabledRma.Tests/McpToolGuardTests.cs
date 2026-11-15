using System.Security.Claims;
using System.Threading.Channels;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Mcp;
using AIEnabledRma.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIEnabledRma.Tests;

/// <summary>
/// Audit and the per-minute budget live on the tools/call request, not inside the customer tools,
/// so the test that matters is the one that never mentions a customer tool: a device tool called
/// through a real client must write a row and must be stoppable by the budget. If this passes only
/// because a device tool remembered to wrap itself, the next tool to be added will not be covered.
/// </summary>
public sealed class McpToolGuardTests
{
    private const string Serial = "AX-1001";

    /// <summary>
    /// A server and a client over a pair of in-memory channels: the real request pipeline
    /// (including the filter) without a process, a socket, or a timeout to make it flaky.
    /// </summary>
    private sealed class LoopbackTransport(Channel<JsonRpcMessage> inbox) : ITransport, IAsyncDisposable
    {
        public LoopbackTransport Peer { get; set; } = null!;

        public string SessionId => "loopback";

        public ChannelReader<JsonRpcMessage> MessageReader => inbox.Reader;

        public ValueTask DisposeAsync()
        {
            inbox.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken) =>
            await Peer.WriteAsync(message, cancellationToken);

        public Task WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken) =>
            inbox.Writer.WriteAsync(message, cancellationToken).AsTask();
    }

    private sealed class LoopbackClientTransport(ITransport transport) : IClientTransport
    {
        public string Name => "loopback";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(transport);
    }

    private sealed record Harness(
        McpClient Client,
        FakeToolAuditSink Audit,
        FakeDeviceRepository Devices);

    private static async Task<Harness> StartAsync(McpBudgetOptions? budget = null)
    {
        var audit = new FakeToolAuditSink();
        var devices = new FakeDeviceRepository();
        var device = TestData.Device(serial: Serial, end: new DateOnly(2027, 1, 1));
        devices.Devices.Add(device);
        devices.LookupResult = new DeviceLookupResult { Device = device, IsInWarranty = true, MatchScore = 1d };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(budget ?? new McpBudgetOptions()));
        services.AddSingleton<CallBudget>();
        services.AddScoped<IToolAuditSink>(_ => audit);
        services.AddSingleton<IDeviceRepository>(devices);
        services.AddSingleton<IRmaRepository>(new FakeRmaRepository());
        services.AddScoped<DeviceLookupTools>();

        services.AddMcpServer().WithTools<DeviceLookupTools>();
        services.AddToolCallGuard();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        var toServer = Channel.CreateUnbounded<JsonRpcMessage>();
        var toClient = Channel.CreateUnbounded<JsonRpcMessage>();
        var serverSide = new LoopbackTransport(toServer);
        var clientSide = new LoopbackTransport(toClient);
        serverSide.Peer = clientSide;
        clientSide.Peer = serverSide;

        var server = McpServer.Create(serverSide, options, NullLoggerFactory.Instance, provider);

        _ = server.RunAsync(TestContext.Current.CancellationToken);

        var client = await McpClient.CreateAsync(
            new LoopbackClientTransport(clientSide),
            loggerFactory: NullLoggerFactory.Instance,
            cancellationToken: TestContext.Current.CancellationToken);

        return new Harness(client, audit, devices);
    }

    private static async Task<McpClientTool> ToolAsync(McpClient client, string name) =>
        (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
        .Single(t => t.Name == name);

    [Fact]
    public async Task A_device_tool_writes_one_audit_row()
    {
        var harness = await StartAsync();
        var tool = await ToolAsync(harness.Client, "find_device");

        var result = await tool.CallAsync(
            new Dictionary<string, object?> { ["identifier"] = Serial },
            progress: null,
            options: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);

        var row = Assert.Single(harness.Audit.Records);
        Assert.Equal("find_device", row.Tool);
        Assert.Equal(ToolCallGuard.CallerName, row.Caller);
        Assert.False(row.Refused);
        Assert.Contains(row.ArgumentHashes, h => h.StartsWith("identifier:", StringComparison.Ordinal));

        // The serial itself must not be in the row: the hash is what makes the trail safe to keep.
        Assert.DoesNotContain(
            Serial,
            string.Join('\n', row.ArgumentHashes),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_device_tool_is_throttled_by_the_minute_budget_before_it_looks()
    {
        var harness = await StartAsync(new McpBudgetOptions { CallsPerMinute = 1 });
        var tool = await ToolAsync(harness.Client, "find_device");
        var arguments = new Dictionary<string, object?> { ["identifier"] = Serial };

        var first = await tool.CallAsync(
            arguments,
            progress: null,
            options: null,
            cancellationToken: TestContext.Current.CancellationToken);

        var second = await tool.CallAsync(
            arguments,
            progress: null,
            options: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, first.IsError);
        Assert.True(second.IsError);
        Assert.Contains(
            "limit",
            Assert.IsType<TextContentBlock>(second.Content[0]).Text,
            StringComparison.OrdinalIgnoreCase);

        // Refused before the work: the device table was read once, not twice.
        Assert.Equal(1, harness.Devices.LookupCallCount);

        Assert.Equal(2, harness.Audit.Records.Count);
        Assert.False(harness.Audit.Records[0].Refused);
        Assert.Equal("find_device", harness.Audit.Records[1].Tool);
        Assert.True(harness.Audit.Records[1].Refused);
    }

    [Fact]
    public async Task A_tool_response_reports_how_many_matches_it_returned()
    {
        var harness = await StartAsync();
        harness.Devices.Devices.Add(TestData.Device(serial: "AX-1002", end: new DateOnly(2027, 1, 1)));
        var tool = await ToolAsync(harness.Client, "search_devices");

        await tool.CallAsync(
            new Dictionary<string, object?> { ["productName"] = "Widget" },
            progress: null,
            options: null,
            cancellationToken: TestContext.Current.CancellationToken);

        // Two devices in the fake catalog, so two matches; the row records what the tool said it
        // found rather than the number of content blocks.
        var row = Assert.Single(harness.Audit.Records);
        Assert.Equal("search_devices", row.Tool);
        Assert.Equal(2, row.ResultCount);
    }

    /// <summary>
    /// A request that can change identity between assertions, so one singleton budget sees two
    /// callers the way the HTTP transport shows it two tokens.
    /// </summary>
    private sealed class RequestThatChanges : Microsoft.AspNetCore.Http.IHttpContextAccessor
    {
        public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }

        public void SignIn(string client, string subject) => HttpContext =
            new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("client_id", client), new Claim("sub", subject)],
                    authenticationType: "test")),
            };
    }

    [Fact]
    public void Two_callers_spend_their_own_budgets_even_though_the_budget_is_a_singleton()
    {
        // The HTTP transport registers one CallBudget for the whole process. Keyed on the process
        // it would be a shared allowance: the first agent to hit the minute would stop every
        // other agent, and the lookup cap would say nothing about what one agent had walked.
        var request = new RequestThatChanges();
        var budget = new CallBudget(
            Options.Create(new McpBudgetOptions { CallsPerMinute = 1, CustomerLookupsPerSession = 1 }),
            TimeProvider.System,
            new HttpContextCallerIdentity(request));

        request.SignIn("agent-a", "subject-a");
        Assert.True(budget.TryConsumeCall(out _));
        Assert.False(budget.TryConsumeCall(out var spent));
        Assert.NotNull(spent);
        Assert.Contains("limit", spent, StringComparison.OrdinalIgnoreCase);

        // A different token, in the same minute, through the same singleton: its own window.
        request.SignIn("agent-b", "subject-b");
        Assert.True(budget.TryConsumeCall(out _));

        // And A is still where it was, so the allowance was spent by A and not lent to B.
        request.SignIn("agent-a", "subject-a");
        Assert.False(budget.TryConsumeCall(out _));

        // The same for the lookup cap: walking the customer table must count against the caller
        // that did it, not against whoever asks next.
        request.SignIn("agent-b", "subject-b");
        Assert.True(budget.TryConsumeCustomerLookup(out _));
        Assert.False(budget.TryConsumeCustomerLookup(out var lookups));
        Assert.NotNull(lookups);
        Assert.Contains("customer lookups", lookups, StringComparison.OrdinalIgnoreCase);

        request.SignIn("agent-a", "subject-a");
        Assert.True(budget.TryConsumeCustomerLookup(out _));
    }
}
