using AIEnabledRma.Data;
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

builder.Services.AddScoped<DeviceLookupTools>();
builder.Services.AddScoped<CustomerLookupTools>();
builder.Services.AddScoped<PolicyTools>();

await builder.Build().RunAsync();

/// <summary>Present so the test project can reference this assembly's generated entry point.</summary>
public partial class Program
{
    protected Program() { }
}
