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
    /// <summary>The CORS policy derived from <see cref="McpHttpOptions.AllowedOrigins"/>.</summary>
    public const string CorsPolicyName = "mcp-browser-clients";
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
        builder.Services.AddRmaMcpServices(builder.Configuration, builder.Environment);
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        builder.Services.AddMcpHttpAuthentication(http, builder.Environment.IsDevelopment());

        // The origin allow-list is also the CORS policy. It is the set of pages the operator said
        // may drive these tools, and without CORS headers the list could only ever refuse browsers
        // rather than admit them: an allowed page's preflight would be answered by nothing, so no
        // browser client could call the endpoint at all.
        //
        // An empty list configures a policy that admits no origin, which is the same meaning it has
        // in the check below. Both read the one setting, so a page cannot be allowed by one and
        // refused by the other.
        builder.Services.AddCors(options => options.AddPolicy(
            CorsPolicyName,
            policy => policy
                .WithOrigins(http.AllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                // A browser client cannot read a header it was not told about, and the session id
                // is the one the transport may hand back on initialize.
                .WithExposedHeaders("Mcp-Session-Id")));

        configureHost?.Invoke(builder.WebHost);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        // First, so a preflight from an allowed origin is answered here rather than falling through
        // to the check below, which sees an OPTIONS request with no token and no route to run.
        app.UseCors(CorsPolicyName);

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

        // Readiness, before the token check and with nothing behind it. A compose health probe runs
        // inside the container with no token to present, and this host has no corpus or model detail
        // worth protecting — unlike the web host's /health, which names both behind an operator key.
        // It answers 200 from the moment the pipeline is serving, which is the thing a proxy should
        // wait for before it sends traffic here.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapMcp(http.Path).RequireAuthorization(McpHttpAuthentication.PolicyName);

        // RFC 9728 discovery, served without a token because it describes how to get one. Both
        // addresses exist: the path-specific one the 401 names and the root one an MCP client falls
        // back to when it ignores the header. Distinct, because a host whose endpoint is at the root
        // maps the same path twice and ASP.NET refuses a duplicate route at startup.
        foreach (var path in new[]
                 {
                     ProtectedResourceMetadata.WellKnownPath,
                     ProtectedResourceMetadata.PathFor(http.Path),
                 }.Distinct(StringComparer.Ordinal))
        {
            app.MapGet(
                path,
                (HttpContext context) =>
                    Results.Json(ProtectedResourceMetadata.DocumentFor(context.Request, http)));
        }

        return app;
    }

    public static async Task RunAsync(string[] args)
    {
        var app = Build(args);

        await app.RunAsync();
    }
}
