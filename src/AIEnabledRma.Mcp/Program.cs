using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Mcp;
using AIEnabledRma.Mcp.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// stdout is the JSON-RPC channel. Anything else written there corrupts the stream, so all
// logging is redirected to stderr. The default console logger writes to stdout, which
// makes an MCP host fail to parse the first response frame.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

builder.Services.AddRmaDbContext(builder.Configuration);
builder.Services.AddRmaData(builder.Configuration);

// Every tool call is audited and counted against the session's per-minute budget at one choke
// point (the tools/call filter), so a tool added later is covered without being wrapped. The
// customer tools additionally cap customer lookups per session.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<McpBudgetOptions>()
    .Bind(builder.Configuration.GetSection(McpBudgetOptions.SectionName));
builder.Services.AddSingleton<CallBudget>();
builder.Services.AddScoped<IToolAuditSink, EfToolAuditSink>();
builder.Services.AddToolCallGuard();

builder.Services.AddScoped<DeviceLookupTools>();
builder.Services.AddScoped<CustomerLookupTools>();
builder.Services.AddScoped<PolicyTools>();

await builder.Build().RunAsync();

/// <summary>Present so the test project can reference this assembly's generated entry point.</summary>
public partial class Program
{
    protected Program() { }
}
