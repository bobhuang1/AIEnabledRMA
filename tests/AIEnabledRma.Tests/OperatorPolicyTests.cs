using System.Reflection;
using System.Security.Claims;
using AIEnabledRma.Ai;
using AIEnabledRma.Rag;
using AIEnabledRma.Web.Controllers;
using AIEnabledRma.Web.Security;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// The auth gate proves Keycloak mints <c>rma:operator</c> as a flat <c>roles</c> claim; these tests
/// prove the host's <c>/Operator</c> policy admits that claim and refuses an authenticated principal
/// without it.
/// </summary>
/// <remarks>
/// A correct token that the host does not read is a sign-in that reaches nothing, and it fails in the
/// least visible way available: the identity provider reports success, the cookie is issued, and every
/// page redirects back to sign-in. Nothing in either process says which side is wrong, so the two ends
/// of the claim — the realm's mapper and the policy that reads it — are pinned here together. If the
/// policy stops reading <c>roles</c>, or the role it demands drifts from the one the realm grants, one
/// of these facts fails.
/// </remarks>
public sealed class OperatorPolicyTests
{
    /// <summary>The realm role the auth gate asserts in an operator's token.</summary>
    private const string RealmRole = "rma:operator";

    private sealed class HostFactory : WebApplicationFactory<global::WebAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing, so the host boots without the production-only guards, and an identity provider
            // is configured because the OpenID Connect scheme is only registered when one is.
            builder.UseEnvironment("Testing");
            builder.UseSetting("OperatorAuth:Authority", "https://idp.example.com/realms/rma");
            builder.UseSetting("OperatorAuth:ClientId", "rma-operator");

            // Both are startup rules the host applies in every non-Development environment; without
            // them the host would stop before any policy could be asked a question.
            builder.UseSetting(
                $"{FakePaymentGatewayOptions.SectionName}:{nameof(FakePaymentGatewayOptions.AllowInProduction)}",
                "true");
            builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, "operator-policy-tests-key");
        }
    }

    [Fact]
    public async Task An_operator_principal_with_the_roles_claim_is_admitted()
    {
        using var factory = new HostFactory();

        var result = await AuthorizeAsync(factory, Principal(new Claim("roles", RealmRole)));

        Assert.True(result.Succeeded, "the policy refused a principal carrying the realm's operator role.");
    }

    /// <summary>
    /// Authenticated is not authorised: the claim is what the policy decides on, and a principal
    /// carrying the name but not the role must not reach the back office.
    /// </summary>
    [Fact]
    public async Task An_authenticated_principal_without_the_roles_claim_is_denied()
    {
        using var factory = new HostFactory();

        var result = await AuthorizeAsync(factory, Principal());

        Assert.False(result.Succeeded, "the policy admitted a signed-in principal with no operator role.");
    }

    /// <summary>
    /// Keycloak's own role claim nests the roles under <c>realm_access</c>, and a token in that shape
    /// is a correct sign-in that grants nothing here. That is why the realm carries the
    /// <c>operator-roles</c> mapper, and why accepting the nested form would hide the mapper going
    /// missing.
    /// </summary>
    [Fact]
    public async Task The_nested_realm_access_shape_alone_does_not_count()
    {
        using var factory = new HostFactory();

        var nested = new Claim("realm_access", $$"""{"roles":["{{RealmRole}}"]}""", "JSON");
        var result = await AuthorizeAsync(factory, Principal(nested));

        Assert.False(result.Succeeded, "the policy read Keycloak's nested role claim, not the flat one.");
    }

    /// <summary>
    /// The token half of the same rule: the OpenID Connect handler passes the <c>roles</c> claim into
    /// the operator cookie under its own name and treats it as the role claim, and the role the policy
    /// demands is the one the realm grants. A mapper that emits a claim the handler renames, or a
    /// host configured for another role, each breaks sign-in without breaking any other test.
    /// </summary>
    [Fact]
    public void The_host_maps_the_flat_roles_claim_and_demands_the_realm_role()
    {
        using var factory = new HostFactory();

        var oidc = factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OperatorAuthOptions.OidcScheme);

        // MapInboundClaims rewrites "roles" to the long WS-Fed URI, which the policy's claim check
        // does not read. Off, the token's claim name survives to the cookie unchanged.
        Assert.False(oidc.MapInboundClaims);
        Assert.Equal(OperatorAuthOptions.CookieScheme, oidc.SignInScheme);
        Assert.Equal("roles", oidc.TokenValidationParameters.RoleClaimType);

        var options = factory.Services.GetRequiredService<IOptions<OperatorAuthOptions>>().Value;

        Assert.Equal(RealmRole, options.RequiredRole);
    }

    /// <summary>
    /// The policy these tests exercise is the one the back office is guarded by, so an admitted
    /// principal reaches the controller and a denied one does not.
    /// </summary>
    [Fact]
    public void The_operator_area_is_guarded_by_that_policy()
    {
        var authorize = typeof(OperatorController).GetCustomAttribute<AuthorizeAttribute>(inherit: true);

        Assert.NotNull(authorize);
        Assert.Equal(OperatorAuthOptions.Policy, authorize.Policy);
    }

    /// <summary>A principal shaped like the operator cookie: the identity provider's claims, the name.</summary>
    private static ClaimsPrincipal Principal(params Claim[] extra) =>
        new(new ClaimsIdentity(
            [
                new Claim("sub", "operator-demo"),
                new Claim("preferred_username", "operator-demo"),
                .. extra,
            ],
            OperatorAuthOptions.CookieScheme,
            "preferred_username",
            "roles"));

    private static async Task<AuthorizationResult> AuthorizeAsync(
        WebApplicationFactory<global::WebAppAnchor> factory,
        ClaimsPrincipal principal)
    {
        using var scope = factory.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        return await authorization.AuthorizeAsync(principal, resource: null, OperatorAuthOptions.Policy);
    }
}
