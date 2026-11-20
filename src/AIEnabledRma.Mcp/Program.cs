using AIEnabledRma.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

// Two transports over the same tools and the same ToolCallGuard:
//   stdio (default) for a developer's local agent, the caller being whoever launched the process;
//   HTTP (--http or Mcp:Transport=http) for remote agents, behind OAuth bearer tokens.
var bootstrap = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

if (McpHttpHost.IsSelected(args, bootstrap))
{
    var web = WebApplication.CreateBuilder(args);
    if (web.Environment.IsProduction())
    {
        AIEnabledRma.Data.ProductionConfiguration.EnsureSafe(web.Configuration);
    }

    McpHttpHost.Configure(web);
    var app = web.Build();
    McpHttpHost.Map(app);
    await app.RunAsync();
    return;
}

var builder = Host.CreateApplicationBuilder(args);
if (builder.Environment.IsProduction())
{
    AIEnabledRma.Data.ProductionConfiguration.EnsureSafe(builder.Configuration);
}

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
