using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AIEnabledRma.Web.Security;

/// <summary>Sign-in for the operator back office (P10).</summary>
public sealed class OperatorAuthOptions
{
    public const string SectionName = "OperatorAuth";

    public const string CookieScheme = "operator";
    public const string OidcScheme = "operator-oidc";
    public const string Policy = "operator";

    /// <summary>OpenID Connect issuer (the same identity provider as the MCP server, decision D2).</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>From the environment or a secret store only.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Role (or scope) an operator's token must carry.</summary>
    public string RequiredRole { get; set; } = "rma:operator";

    /// <summary>
    /// A one-click sign-in as a fixed local operator, for Development and the test host only. The
    /// host refuses to start with it switched on in any other environment.
    /// </summary>
    public bool DevSignIn { get; set; }

    public bool OidcConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}

public static class OperatorAuthSetup
{
    public static IServiceCollection AddOperatorAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(OperatorAuthOptions.SectionName).Get<OperatorAuthOptions>() ?? new OperatorAuthOptions();
        services.AddOptions<OperatorAuthOptions>().Bind(configuration.GetSection(OperatorAuthOptions.SectionName));

        if (options.DevSignIn && !(environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            throw new InvalidOperationException(
                $"OperatorAuth:DevSignIn is on in the '{environment.EnvironmentName}' environment. It signs anyone in as an "
                + "operator and is only allowed in Development. Configure OperatorAuth:Authority and ClientId instead.");
        }

        var auth = services
            .AddAuthentication(OperatorAuthOptions.CookieScheme)
            .AddCookie(OperatorAuthOptions.CookieScheme, cookie =>
            {
                cookie.Cookie.Name = "rma-operator";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = environment.IsDevelopment() || environment.IsEnvironment("Testing")
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                cookie.Cookie.Path = "/Operator";
                cookie.LoginPath = "/Operator/SignIn";
                cookie.AccessDeniedPath = "/Operator/Denied";
                cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
                cookie.SlidingExpiration = false;
            });

        if (options.OidcConfigured)
        {
            auth.AddOpenIdConnect(OperatorAuthOptions.OidcScheme, oidc =>
            {
                oidc.SignInScheme = OperatorAuthOptions.CookieScheme;
                oidc.Authority = options.Authority;
                oidc.ClientId = options.ClientId;
                oidc.ClientSecret = options.ClientSecret;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.MapInboundClaims = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.CallbackPath = "/Operator/signin-oidc";
                oidc.SignedOutCallbackPath = "/Operator/signout-oidc";
                oidc.TokenValidationParameters.NameClaimType = "preferred_username";
                oidc.TokenValidationParameters.RoleClaimType = "roles";
                oidc.RequireHttpsMetadata = !environment.IsDevelopment();
            });
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(OperatorAuthOptions.Policy, policy => policy
                .AddAuthenticationSchemes(OperatorAuthOptions.CookieScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasRole(context.User, options.RequiredRole)));

        return services;
    }

    public static bool HasRole(ClaimsPrincipal user, string role) =>
        user.Claims.Any(c =>
            (c.Type is "roles" or "role" or ClaimTypes.Role && c.Value == role)
            || (c.Type is "scope" or "scp" && c.Value.Split(' ').Contains(role, StringComparer.Ordinal)));

    /// <summary>The name recorded in the audit trail for the signed-in operator.</summary>
    public static string OperatorName(ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.FindFirst("email")?.Value
        ?? user.FindFirst(ClaimTypes.Name)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? "unknown-operator";
}
