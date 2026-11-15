using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AIEnabledRma.Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace AIEnabledRma.Tests;

/// <summary>
/// The remote transport refuses what it should before a tool is ever reached: no token, the wrong
/// audience, the wrong issuer, a missing scope, an unlisted client, a page from an origin nobody
/// allowed. The tokens here are minted by the test against a key it holds, standing in for the
/// realm's signing key, because what is under test is the validation the host applies to a real
/// token — not the identity provider that issued it.
/// </summary>
public sealed class McpHttpTransportTests
{
    private const string Issuer = "https://identity.test/realms/aienabledrma";
    private const string Audience = "aienabledrma-mcp";
    private const string ReadScope = "rma:read";
    private const string ClientId = "rma-agent";
    private const string AllowedOrigin = "http://localhost:5173";

    private static readonly byte[] SigningKey =
        Encoding.UTF8.GetBytes("aienabledrma-mcp-test-signing-key-not-a-real-secret");

    /// <summary>
    /// The real host, built by the same code a deployment runs, on the in-memory test server so
    /// the request the test sends is the request the server routes.
    /// </summary>
    private static async Task<WebApplication> BuildHostAsync()
    {
        var app = McpHttpHost.Build(
            [
                $"--{McpHttpOptions.SectionName}:Issuer={Issuer}",
                $"--{McpHttpOptions.SectionName}:Audience={Audience}",
                $"--{McpHttpOptions.SectionName}:RequiredScope={ReadScope}",
                $"--{McpHttpOptions.SectionName}:AllowedOrigins:0={AllowedOrigin}",
                $"--{McpHttpOptions.SectionName}:AllowedClientIds:0={ClientId}",
            ],
            host => host.UseTestServer(),
            services => services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    // No realm is running here. The issuer and audience the host validated are
                    // kept; the discovery document is replaced by a key this test controls, so a
                    // token signed with anything else is refused exactly as a forged one would be.
                    options.Authority = null;
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = Issuer,
                        ValidateAudience = true,
                        ValidAudience = Audience,
                        ValidateLifetime = true,
                        RequireExpirationTime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(SigningKey),
                    };
                }));

        await app.StartAsync(TestContext.Current.CancellationToken);

        return app;
    }

    private static string Token(
        string issuer = Issuer,
        string audience = Audience,
        string? scope = ReadScope,
        string? clientId = ClientId)
    {
        var claims = new List<Claim> { new("sub", "agent-1") };

        if (scope is not null)
        {
            claims.Add(new Claim("scope", scope));
        }

        if (clientId is not null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(SigningKey),
                SecurityAlgorithms.HmacSha256));

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpRequestMessage Initialize(string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
                """,
                Encoding.UTF8,
                "application/json"),
        };

        // The streamable transport answers either as JSON or as an event stream; a client has to
        // accept both, and a host that saw only one advertised would be a real client's bug.
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    [Fact]
    public async Task A_request_with_no_token_is_refused_with_401()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(
            Initialize(token: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_minted_for_another_audience_is_refused_with_401()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(
            Initialize(Token(audience: "aienabledrma-web")),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_refused_with_401()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(
            Initialize(Token(issuer: "https://identity.test/realms/somewhere-else")),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_the_read_scope_is_authenticated_but_forbidden()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(
            Initialize(Token(scope: "rma:write")),
            TestContext.Current.CancellationToken);

        // 403 rather than 401: the token is genuine, and the caller's problem is that it was not
        // granted the scope, which is a different fix.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_token_from_a_client_that_is_not_listed_is_forbidden()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(
            Initialize(Token(clientId: "another-service-in-the-realm")),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_page_from_an_origin_that_is_not_allowed_is_forbidden()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(
            Initialize(Token(), origin: "https://evil.example"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_origin_that_only_starts_with_an_allowed_one_is_forbidden()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        // A prefix comparison would accept this; the allow-list is compared exactly.
        using var response = await client.SendAsync(
            Initialize(Token(), origin: AllowedOrigin + ".evil.example"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string? AllowedOriginOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values)
            ? string.Join(",", values)
            : null;

    [Fact]
    public async Task An_allowed_origin_can_preflight_the_endpoint()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        preflight.Headers.Add("Origin", AllowedOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");

        using var response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

        // A preflight carries no token and matches no POST route: before the policy existed this
        // was the one request the origin check answered, so an allowed page could never get as far
        // as sending the real one.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, AllowedOriginOf(response));

        var allowedHeaders = response.Headers.TryGetValues("Access-Control-Allow-Headers", out var values)
            ? string.Join(",", values)
            : string.Empty;

        Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_allowed_origin_can_read_the_headers_the_transport_returns()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(
            Initialize(Token(), AllowedOrigin),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AllowedOrigin, AllowedOriginOf(response));

        // Without the expose header a page can send Mcp-Session-Id back but cannot read it, which
        // breaks the transport the moment the SDK starts issuing sessions.
        var exposed = response.Headers.TryGetValues("Access-Control-Expose-Headers", out var values)
            ? string.Join(",", values)
            : string.Empty;

        Assert.Contains("Mcp-Session-Id", exposed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_preflight_from_a_disallowed_origin_grants_nothing()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

        // The framework answers every preflight 204; what decides whether the browser proceeds is
        // the absence of an allow-origin header. So the assertion is that the policy granted
        // nothing here — and the real request from that origin is the 403 in
        // A_page_from_an_origin_that_is_not_allowed_is_forbidden, which is what a browser would
        // send if it ignored this answer.
        Assert.Null(AllowedOriginOf(response));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
    }

    [Fact]
    public async Task An_allowed_origin_reaches_the_tools_with_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var initialize = await client.SendAsync(
            Initialize(Token(), AllowedOrigin),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);

        // The transport may or may not issue a session; when it does, later requests carry it.
        // Asserting on the header would test the SDK's choice rather than this host's policy.
        var sessionId = initialize.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.Single()
            : null;

        var list = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
                Encoding.UTF8,
                "application/json"),
        };

        list.Headers.Accept.ParseAdd("application/json");
        list.Headers.Accept.ParseAdd("text/event-stream");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());

        if (sessionId is not null)
        {
            list.Headers.Add("Mcp-Session-Id", sessionId);
        }

        using var response = await client.SendAsync(list, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("check_warranty_coverage", body, StringComparison.Ordinal);
    }
}

/// <summary>
/// The audit row must say which caller made a call: the token's client on HTTP, the local session
/// on stdio. Checked without a database by asking the identity what it would report.
/// </summary>
public sealed class CallerIdentityTests
{
    private static HttpContextCallerIdentity IdentityFor(ClaimsPrincipal? user)
    {
        var http = new DefaultHttpContext();

        if (user is not null)
        {
            http.User = user;
        }

        return new HttpContextCallerIdentity(new HttpContextAccessor { HttpContext = http });
    }

    [Fact]
    public void An_authenticated_token_is_recorded_by_client_and_subject()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "agent-1"),
                new Claim("client_id", "rma-agent"),
            ],
            authenticationType: "Bearer"));

        Assert.Equal("rma-agent:agent-1", IdentityFor(user).Describe());
    }

    [Fact]
    public void A_stdio_session_is_recorded_as_the_local_caller()
    {
        Assert.Equal(ToolCallGuard.CallerName, IdentityFor(user: null).Describe());
    }
}

/// <summary>
/// The transport's own preconditions, checked without a host: each of these is a configuration a
/// deployment could plausibly arrive at, and each would serve the tools to more callers than the
/// operator intended.
/// </summary>
public sealed class McpHttpOptionsTests
{
    private static McpHttpOptions Usable() => new()
    {
        Issuer = "https://identity.test/realms/aienabledrma",
        Audience = "aienabledrma-mcp",
        RequiredScope = "rma:read",
        AllowedClientIds = ["rma-agent"],
    };

    [Fact]
    public void An_empty_client_allow_list_is_refused()
    {
        var options = Usable();
        options.AllowedClientIds = [];

        var error = Assert.Throws<InvalidOperationException>(
            () => options.EnsureUsable(isDevelopment: false));

        Assert.Contains("AllowedClientIds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_http_issuer_is_refused_outside_development()
    {
        var options = Usable();
        options.Issuer = "http://localhost:8080/realms/aienabledrma";

        var error = Assert.Throws<InvalidOperationException>(
            () => options.EnsureUsable(isDevelopment: false));

        Assert.Contains("https", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wildcard_origin_is_refused()
    {
        var options = Usable();
        options.AllowedOrigins = ["*"];

        var error = Assert.Throws<InvalidOperationException>(
            () => options.EnsureUsable(isDevelopment: false));

        Assert.Contains("AllowedOrigins", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_scope_is_refused()
    {
        var options = Usable();
        options.RequiredScope = " ";

        Assert.Throws<InvalidOperationException>(() => options.EnsureUsable(isDevelopment: false));
    }

    [Fact]
    public void The_local_keycloak_default_is_usable_in_development()
    {
        // The committed default: the compose file's realm, http and all, is exactly what a
        // developer's machine should accept.
        var options = Usable();
        options.Issuer = "http://localhost:8080/realms/aienabledrma";

        options.EnsureUsable(isDevelopment: true);
    }
}
