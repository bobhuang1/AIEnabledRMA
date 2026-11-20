using AIEnabledRma.Mcp;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout is the JSON-RPC channel. Anything else written there corrupts the stream, so all
// logging is redirected to stderr. The default console logger writes to stdout, which
// makes an MCP host fail to parse the first response frame.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Tools, data access, and the guard that checks permission and budgets and audits every call.
builder.Services.AddRmaMcpServer(builder.Configuration)
    .WithStdioServerTransport();

await builder.Build().RunAsync();

/// <summary>Present so the test project can reference this assembly's generated entry point.</summary>
public partial class Program
{
    protected Program() { }
}
