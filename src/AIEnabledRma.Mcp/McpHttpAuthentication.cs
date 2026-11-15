using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace AIEnabledRma.Mcp;

/// <summary>
/// Authentication and the single authorization policy the remote transport uses: a token from the
/// realm's issuer, minted for this audience, presented by a listed client, carrying the read scope.
/// </summary>
/// <remarks>
/// There is deliberately no switch that turns this off. stdio is the unauthenticated transport and
/// it stays the default; if HTTP could also run unauthenticated, the one configuration mistake that
/// matters — shipping <c>Mcp:Transport=http</c> without the identity provider — would serve the
/// customer and device tools to the network.
/// </remarks>
public static class McpHttpAuthentication
{
    /// <summary>The policy every MCP request must satisfy.</summary>
    public const string PolicyName = "mcp-tools";

    public static IServiceCollection AddMcpHttpAuthentication(
        this IServiceCollection services,
        McpHttpOptions http,
        bool isDevelopment)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = http.Issuer;

                // A local Keycloak serves its discovery document over http; anything outside
                // Development has already been refused an http issuer by EnsureUsable.
                options.RequireHttpsMetadata = !isDevelopment;

                options.Audience = http.Audience;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = http.Issuer,
                    ValidateAudience = true,
                    ValidAudience = http.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = "sub",
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    HasScope(context.User, http.RequiredScope)
                    && IsListedClient(context.User, http.AllowedClientIds)));

        return services;
    }

    /// <summary>
    /// The token carries scopes as a space-separated <c>scope</c> claim; some issuers use <c>scp</c>,
    /// and Keycloak emits one or the other depending on how the client is configured.
    /// </summary>
    private static bool HasScope(ClaimsPrincipal user, string requiredScope) =>
        user.Claims
            .Where(claim => claim.Type is "scope" or "scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.Ordinal);

    /// <summary>
    /// <c>azp</c> is the authorized party Keycloak puts in when a client acts on someone's behalf,
    /// which is the value the allow-list is really about; <c>client_id</c> is the more direct claim
    /// and is what most issuers mint. Either naming an allowed client is enough.
    /// </summary>
    private static bool IsListedClient(ClaimsPrincipal user, string[] allowedClientIds)
    {
        var clientId = user.FindFirst("client_id")?.Value ?? user.FindFirst("azp")?.Value;

        return clientId is not null && allowedClientIds.Contains(clientId, StringComparer.Ordinal);
    }
}
