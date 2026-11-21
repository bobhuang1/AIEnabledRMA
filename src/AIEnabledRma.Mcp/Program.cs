using AIEnabledRma.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

if (McpTransportMode.Read(args) == McpTransportMode.Http)
{
    await McpHttpHost.RunAsync(args);
}
else
{
    var builder = Host.CreateApplicationBuilder(args);

    // stdout is the JSON-RPC channel. Anything else written there corrupts the stream, so all
    // logging is redirected to stderr. The default console logger writes to stdout, which
    // makes an MCP host fail to parse the first response frame.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddRmaMcpServices(builder.Configuration, builder.Environment);
    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

/// <summary>Present so the test project can reference this assembly's generated entry point.</summary>
public partial class Program
{
    protected Program() { }
}
