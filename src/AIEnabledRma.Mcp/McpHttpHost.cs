using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace AIEnabledRma.Mcp;

/// <summary>
/// The remote transport: streamable HTTP, behind token validation, an origin allow-list and the
/// tools' own guard. stdio remains the default and shares every service registered here.
/// </summary>
public static class McpHttpHost
{
    /// <summary>
    /// Builds the remote host without running it. Tests build it with <c>UseTestServer</c> so the
    /// real pipeline — routing, the origin check, authentication, authorization, the MCP endpoint —
    /// is exercised in process, and the 401s and 403s below are the ones a deployment produces.
    /// </summary>
    public static WebApplication Build(
        string[] args,
        Action<IWebHostBuilder>? configureHost = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // stdout carries nothing on this transport, but a host log line and an SSE frame sharing
        // one stream is still a mistake worth not making; stderr keeps the log readable.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        var http = new McpHttpOptions();
        builder.Configuration.GetSection(McpHttpOptions.SectionName).Bind(http);
        http.EnsureUsable(builder.Environment.IsDevelopment());

        builder.Services.AddSingleton(http);
        builder.Services.AddRmaMcpServices(builder.Configuration);
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        builder.Services.AddMcpHttpAuthentication(http, builder.Environment.IsDevelopment());

        configureHost?.Invoke(builder.WebHost);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        // Before authentication, so a page from an origin that was never allowed is refused
        // without the host validating, logging or rate-limiting its token first. A request with no
        // Origin header is not a browser request and passes: only a page is bound by the scheme,
        // host and port the browser attaches.
        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();

            if (origin.Length > 0 && !http.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapMcp(http.Path).RequireAuthorization(McpHttpAuthentication.PolicyName);

        return app;
    }

    public static async Task RunAsync(string[] args)
    {
        var app = Build(args);

        await app.RunAsync();
    }
}
