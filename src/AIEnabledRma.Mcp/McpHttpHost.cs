using AIEnabledRma.Mcp.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace AIEnabledRma.Mcp;

/// <summary>Settings for the remote (streamable HTTP) transport.</summary>
public sealed class McpHttpOptions
{
    public const string SectionName = "McpHttp";

    /// <summary>The identity provider's issuer URL (Keycloak realm, Entra tenant). HTTPS outside Development.</summary>
    public string? Authority { get; set; }

    /// <summary>Audience the access token must carry.</summary>
    public string Audience { get; set; } = "aienabledrma-mcp";

    /// <summary>Scope every caller must hold to reach any tool.</summary>
    public string RequiredScope { get; set; } = "rma:read";

    /// <summary>Client ids (<c>azp</c>/<c>client_id</c>) allowed to call. Empty refuses everyone.</summary>
    public IList<string> AllowedClientIds { get; set; } = [];

    /// <summary>
    /// Browser origins allowed to call. A request without an Origin header (a server-side agent)
    /// is allowed; one with an Origin not on this list is refused, which blocks DNS-rebinding and
    /// cross-site calls from a browser.
    /// </summary>
    public IList<string> AllowedOrigins { get; set; } = [];

    /// <summary>The public URL of this server's MCP endpoint, published in the resource metadata.</summary>
    public string? ResourceUrl { get; set; }

    /// <summary>Path the MCP endpoint is mapped at.</summary>
    public string Path { get; set; } = "/mcp";
}

/// <summary>
/// The remote MCP server: the same tools and <see cref="ToolCallGuard"/> as stdio, over
/// streamable HTTP, behind JWT bearer authentication with RFC 9728 protected-resource metadata
/// (D2). Every request needs a token for <see cref="McpHttpOptions.Audience"/> carrying
/// <see cref="McpHttpOptions.RequiredScope"/> from an allowed client, and the caller's identity
/// flows into the audit trail and the budgets.
/// </summary>
public static class McpHttpHost
{
    public const string PolicyName = "mcp-caller";

    /// <summary>True when configuration selects the HTTP transport (<c>Mcp:Transport=http</c> or <c>--http</c>).</summary>
    public static bool IsSelected(string[] args, IConfiguration configuration) =>
        args.Contains("--http", StringComparer.OrdinalIgnoreCase)
        || string.Equals(configuration["Mcp:Transport"], "http", StringComparison.OrdinalIgnoreCase);

    public static WebApplicationBuilder Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var http = builder.Configuration.GetSection(McpHttpOptions.SectionName).Get<McpHttpOptions>() ?? new McpHttpOptions();
        builder.Services.AddOptions<McpHttpOptions>()
            .Bind(builder.Configuration.GetSection(McpHttpOptions.SectionName))
            .Validate(
                o => builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
                    || (Uri.TryCreate(o.Authority, UriKind.Absolute, out var authority) && authority.Scheme == Uri.UriSchemeHttps),
                "McpHttp:Authority must be an https URL outside Development.")
            .Validate(o => o.AllowedClientIds.Count > 0, "McpHttp:AllowedClientIds must list at least one client.")
            .ValidateOnStart();

        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.Authority = http.Authority;
                options.Audience = http.Audience;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.ValidAudience = http.Audience;
            })
            .AddMcp(options =>
            {
                options.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = http.ResourceUrl ?? string.Empty,
                    AuthorizationServers = http.Authority is null ? [] : [http.Authority],
                    ScopesSupported = [http.RequiredScope],
                    BearerMethodsSupported = ["header"],
                    ResourceName = "AIEnabledRMA returns tools",
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                {
                    var caller = McpCaller.From(context.User, new McpSecurityOptions());
                    return caller.Has(http.RequiredScope)
                        && http.AllowedClientIds.Contains(caller.Id, StringComparer.Ordinal);
                }));

        // Stateless: every request is authenticated and authorised on its own, and nothing about a
        // caller survives in server memory between requests except the guard's budgets.
        builder.Services.AddRmaMcpServer(builder.Configuration)
            .WithHttpTransport(options => options.Stateless = true);

        return builder;
    }

    public static WebApplication Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var http = app.Services.GetRequiredService<IOptions<McpHttpOptions>>().Value;

        // Origin check before authentication, so a browser page on another site cannot even
        // probe the endpoint with a user's ambient credentials.
        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !http.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapMcp(http.Path).RequireAuthorization(PolicyName);

        return app;
    }
}
