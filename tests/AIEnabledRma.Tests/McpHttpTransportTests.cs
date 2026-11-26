using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
    private const string SupportScope = "rma:support";
    private const string ClientId = "rma-agent";
    private const string AllowedOrigin = "http://localhost:5173";

    private static readonly byte[] SigningKey =
        Encoding.UTF8.GetBytes("aienabledrma-mcp-test-signing-key-not-a-real-secret");

    /// <summary>
    /// The real host, built by the same code a deployment runs, on the in-memory test server so
    /// the request the test sends is the request the server routes.
    /// </summary>
    private static async Task<WebApplication> BuildHostAsync(
        Action<IServiceCollection>? configureServices = null,
        string? connectionString = null,
        bool overrideValidation = true)
    {
        List<string> settings =
        [
            // The direct host builder starts in Production, where the shared startup check would
            // refuse the committed development password before a single request reached the
            // transport. The production rule is asserted in ProductionConfigurationTests; what these
            // cases measure is the pipeline, so the host runs where a developer runs it.
            "--environment=Development",
            $"--{McpHttpOptions.SectionName}:Issuer={Issuer}",
            $"--{McpHttpOptions.SectionName}:Audience={Audience}",
            $"--{McpHttpOptions.SectionName}:RequiredScope={ReadScope}",
            $"--{McpHttpOptions.SectionName}:AllowedOrigins:0={AllowedOrigin}",
            $"--{McpHttpOptions.SectionName}:AllowedClientIds:0={ClientId}",
        ];

        // Only the audit-table test needs a database. The others build the host with the
        // connection string the committed appsettings carries, which they never use.
        if (connectionString is not null)
        {
            settings.Add($"--ConnectionStrings:Rma={connectionString}");
        }

        var app = McpHttpHost.Build(
            [.. settings],
            host => host.UseTestServer(),
            services =>
            {
                if (overrideValidation)
                {
                    ConfigureValidation(services);
                }

                // A caller can swap the sink and the repositories: a test that needs a tool to run
                // without a database replaces them here, and the swap does not touch the pipeline
                // under test.
                configureServices?.Invoke(services);
            });

        await app.StartAsync(TestContext.Current.CancellationToken);

        return app;
    }

    /// <summary>
    /// Replaces the discovery document with a key this test holds. The host's own validation
    /// parameters are read by the lifetime test below, which is why this is separate from the
    /// factory: overriding them there would hide what the host configures.
    /// </summary>
    private static void ConfigureValidation(IServiceCollection services)
    {
        services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    // No realm is running here. The issuer and audience the host validated are kept;
                    // the discovery document is replaced by a key this test controls, so a token
                    // signed with anything else is refused exactly as a forged one would be.
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

                        // The host allows one minute of skew, and the expired-token case is minted
                        // against that, not against the framework's five-minute default — which
                        // would accept a token that the deployment refuses.
                        ClockSkew = TimeSpan.FromMinutes(1),
                    };
                });
    }

    private static string Token(
        string issuer = Issuer,
        string audience = Audience,
        string? scope = ReadScope,
        string? clientId = ClientId,
        DateTime? expires = null)
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

        // An expiry handed in is how the expired case is minted: the transport allows one minute of
        // clock skew, so anything that expired more than a minute ago is expired to it, without
        // waiting for a real token to age.
        var expiresAt = expires ?? DateTime.UtcNow.AddMinutes(5);

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: expiresAt.AddMinutes(-5),
            expires: expiresAt,
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

    /// <summary>
    /// The readiness probe a deployment runs has no token to present, so <c>/health</c> has to
    /// answer without one, and it has to say nothing beyond liveness: it is the one route on this
    /// host that anyone who can reach the port may read.
    /// </summary>
    [Fact]
    public async Task The_health_endpoint_answers_without_a_token_and_says_nothing_else()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());

        // Exactly one property: a probe that named tools, issuers or allowed clients would be an
        // anonymous inventory of the deployment.
        Assert.Single(json.RootElement.EnumerateObject());
    }

    /// <summary>
    /// The audit row's caller as it reaches the table: a real <c>tools/call</c> over HTTP, a token
    /// naming a client and a subject, and the row read back from <c>mcp_tool_calls</c>.
    /// </summary>
    /// <remarks>
    /// Asserting at the sink proved the identity reached the guard; it did not prove the row exists.
    /// The sink writes through EF into PostgreSQL, and both halves of that — the caller name and the
    /// write — can break while the other is correct: this ran with the real sink and found that an
    /// inbound claim mapping renamed <c>sub</c>, so every HTTP row recorded <c>unknown-subject</c> for
    /// a valid token. Only the row in the table answers the question the audit trail is asked.
    /// The catalogue and the counter are replaced so no seed data is needed; routing, token
    /// validation, the filter, the guard, the identity and the sink are the real ones.
    /// </remarks>
    [Fact]
    public async Task A_tool_call_over_http_writes_the_token_s_client_and_subject_to_the_audit_table()
    {
        await using var database = new LiveDatabase("mcpaudit_http");
        await database.InitializeAsync();

        await using (var setup = AuditContext(database.ConnectionString))
        {
            await SchemaBootstrapper.InitializeAsync(
                setup,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        using var app = await BuildHostAsync(
            services =>
            {
                services.AddSingleton<IDeviceRepository, NoSuchDeviceRepository>();
                services.AddSingleton<IEligibilityService, UnusedEligibilityService>();

                // The production meter counts in PostgreSQL; this test is about the audit row, and
                // its own rows would only add noise to the table being read.
                services.AddSingleton<ICallMeter>(new InMemoryCallMeter());
            },
            database.ConnectionString);

        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"check_warranty_coverage","arguments":{"identifier":"AX2-0001-0001","today":"2026-11-16"}}}
                """,
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var read = AuditContext(database.ConnectionString);

        var row = Assert.Single(await read.McpToolCalls
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("check_warranty_coverage", row.Tool);

        // The token minted above carries client_id rma-agent and sub agent-1, so that is what the
        // row has to say. The stdio name here would mean the host's HttpContext never reached the
        // guard, which is the failure a unit test on the identity class cannot see.
        Assert.Equal($"{ClientId}:agent-1", row.Caller);
        Assert.NotEqual(ToolCallGuard.CallerName, row.Caller);
        Assert.False(row.Refused);
    }

    private static RmaDbContext AuditContext(string connectionString) =>
        new(new DbContextOptionsBuilder<RmaDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    [Fact]
    public async Task A_401_names_the_metadata_document_and_the_document_answers()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(
            Initialize(token: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var challenge = response.Headers.WwwAuthenticate.ToString();

        // Without the pointer a client holds a URL and no way to find the issuer that serves it,
        // which is the gap this closes; the scope is named so it asks for the right one.
        Assert.Contains(
            "resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"",
            challenge,
            StringComparison.Ordinal);
        Assert.Contains($"scope=\"{ReadScope}\"", challenge, StringComparison.Ordinal);

        // Follow it the way a client would, with no token: the document describes how to get one.
        using var metadata = await client.GetAsync(
            "/.well-known/oauth-protected-resource/mcp",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal("application/json", metadata.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(
            await metadata.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var root = document.RootElement;

        // The resource identifier is the endpoint a client connects to, and the issuer is the value
        // the host validates, so a client that follows both converges on the realm it needs.
        Assert.Equal("http://localhost/mcp", root.GetProperty("resource").GetString());
        Assert.Equal(Issuer, root.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal(ReadScope, root.GetProperty("scopes_supported")[0].GetString());
        Assert.Equal("header", root.GetProperty("bearer_methods_supported")[0].GetString());
    }

    [Fact]
    public async Task The_metadata_is_also_at_the_root_well_known_path()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        // MCP clients try the endpoint-specific URI first and fall back to the root when the 401
        // carries no pointer, so both have to answer with the same document.
        using var metadata = await client.GetAsync(
            "/.well-known/oauth-protected-resource",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);

        using var document = JsonDocument.Parse(
            await metadata.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("http://localhost/mcp", document.RootElement.GetProperty("resource").GetString());
        Assert.Equal(Issuer, document.RootElement.GetProperty("authorization_servers")[0].GetString());
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

        // The header parameter has to survive the refusal path, not only the anonymous one: a
        // client whose token is wrong is exactly the client that needs to find the right issuer.
        Assert.Contains(
            "resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"",
            response.Headers.WwwAuthenticate.ToString(),
            StringComparison.Ordinal);
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

    /// <summary>
    /// Expiry is the refusal a wrong audience or a missing scope cannot stand in for.
    /// </summary>
    /// <remarks>
    /// The only place it was proven was the live gate, which waits 65 seconds for a one-second token
    /// to age past the transport's one-minute clock skew. That end-to-end case is worth keeping — it
    /// is the realm's own token, not one this test signed — but as the only proof it made the check
    /// too slow to run on its own, so a change that drops lifetime validation would survive every
    /// fast test. This mints the token already expired and asserts the same 401.
    /// </remarks>
    [Fact]
    public async Task A_token_whose_expiry_has_passed_is_refused_with_401()
    {
        using var app = await BuildHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(
            Initialize(Token(expires: DateTime.UtcNow.AddMinutes(-2))),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The host's own parameters validate expiry, within one minute of skew.
    /// </summary>
    /// <remarks>
    /// The transport tests replace the validation parameters with ones they control — there is no
    /// realm here — so a test that sends an expired token proves the JwtBearer handler refuses it,
    /// not that this host asked it to. This reads the parameters the host configured, before the
    /// replacement, which is the half a change to <c>McpHttpAuthentication</c> can break.
    /// </remarks>
    [Fact]
    public async Task The_host_validates_expiry_with_one_minute_of_skew()
    {
        using var app = await BuildHostAsync(overrideValidation: false);

        var parameters = app.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .TokenValidationParameters;

        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.RequireExpirationTime);
        Assert.Equal(TimeSpan.FromMinutes(1), parameters.ClockSkew);
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

    /// <summary>
    /// D3 on the wire: one tool, one customer, two tokens that differ only in the support scope.
    /// </summary>
    /// <remarks>
    /// The grant is asserted through the real pipeline — routing, token validation, the guard, the
    /// identity and the tool — because the identity, the scope claim and the tool's own check are
    /// three places a correct-looking implementation can disagree. The sink is replaced so the test
    /// needs no database; that swap is also what lets it read the audit row the guard wrote, which is
    /// where the reason has to appear and the argument values must not.
    /// </remarks>
    [Fact]
    public async Task The_unmask_is_refused_to_a_read_token_and_granted_to_a_support_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string Why = "customer called to correct the phone number on their return";

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            FirstName = "Josephine",
            LastName = "Smith",
            Email = "josephine.smith@example.com",
            PhoneNumber = "+1 510 555 0199",
            CreatedAtUtc = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero),
        };

        customer.Addresses.Add(new Address
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Label = "Home",
            Line1 = "2210 Harrison Street",
            City = "Oakland",
            PostalCode = "94612",
            CountryCode = "US",
            IsDefault = true,
        });

        var audit = new FakeToolAuditSink();

        using var app = await BuildHostAsync(
            services =>
            {
                services.AddSingleton<ICustomerRepository>(new FakeCustomerRepository { ById = customer });
                services.AddSingleton<ICallMeter>(new InMemoryCallMeter());
                services.AddScoped<IToolAuditSink>(_ => audit);
            });

        using var client = app.GetTestClient();

        // Serialised rather than typed out: the body nests three levels of braces, and a literal that
        // long is a place to mistype a quote rather than a test.
        static HttpRequestMessage Reveal(string token, Guid customerId) => new(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 11,
                method = "tools/call",
                @params = new
                {
                    name = "reveal_customer_contact",
                    arguments = new { customerId, reason = Why },
                },
            }),
            Headers =
            {
                Authorization = new AuthenticationHeaderValue("Bearer", token),
            },
        };

        using var readOnlyRequest = Reveal(Token(scope: ReadScope), customer.Id);
        readOnlyRequest.Headers.Accept.ParseAdd("application/json");
        readOnlyRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var refused = await client.SendAsync(readOnlyRequest, cancellationToken);
        var refusedBody = await refused.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("support scope", refusedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("josephine.smith@example.com", refusedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Harrison", refusedBody, StringComparison.OrdinalIgnoreCase);

        using var supportRequest = Reveal(Token(scope: $"{ReadScope} {SupportScope}"), customer.Id);
        supportRequest.Headers.Accept.ParseAdd("application/json");
        supportRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var granted = await client.SendAsync(supportRequest, cancellationToken);
        var grantedBody = await granted.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        Assert.Contains("josephine.smith@example.com", grantedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2210 Harrison Street", grantedBody, StringComparison.OrdinalIgnoreCase);

        // The trail keeps the reason as written and hashes everything the call acted on: the row
        // answers why the record was unmasked without becoming a copy of it. Both calls reached the
        // tool, so both rows exist and both carry the reason — what differs is the answer, which is
        // what the assertions above check.
        var rows = audit.Records.Where(r => r.Tool == "reveal_customer_contact").ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(Why, r.Reason));
        Assert.All(rows, r => Assert.Contains(
            r.ArgumentHashes,
            h => h.StartsWith("customerId:", StringComparison.Ordinal)));
        Assert.All(rows, r => Assert.DoesNotContain(
            r.ArgumentHashes,
            h => h.Contains(Why, StringComparison.Ordinal)));
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

    /// <summary>
    /// The precondition is only a refusal if the host asks for it. `EnsureUsable` can be correct and
    /// still be called nowhere, which is the shape a check like this most often takes wrong; so this
    /// builds the host a deployment builds, with both scopes set to `rma:read`, and expects the
    /// startup throw rather than a host that comes up and serves.
    /// </summary>
    /// <remarks>
    /// Production with an https issuer, so the refusal that fires is this one: every other
    /// precondition is satisfied, and a development-shaped host would be a different test. The host
    /// is never started, and the throw happens before anything is bound, so there is nothing to
    /// dispose.
    /// </remarks>
    [Fact]
    public void A_host_built_with_the_read_scope_as_the_support_scope_refuses_to_start()
    {
        var error = Assert.Throws<InvalidOperationException>(() => McpHttpHost.Build(
        [
            "--environment=Production",
            $"--{McpHttpOptions.SectionName}:Issuer={Issuer}",
            $"--{McpHttpOptions.SectionName}:RequiredScope={ReadScope}",
            $"--{McpHttpOptions.SectionName}:SupportScope={ReadScope}",
            $"--{McpHttpOptions.SectionName}:AllowedClientIds:0={ClientId}",
        ]));

        Assert.Contains("SupportScope", error.Message, StringComparison.Ordinal);
        Assert.Contains("RequiredScope", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty catalogue. The tool's miss path still returns a result, which is all the test needs:
    /// the row is written for the call, not for a hit.
    /// </summary>
    private sealed class NoSuchDeviceRepository : IDeviceRepository
    {
        public Task<Device?> GetBySerialAsync(string serialNumber, CancellationToken cancellationToken) =>
            Task.FromResult<Device?>(null);

        public Task<IReadOnlyList<Device>> FindByIdentifierAsync(
            string identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Device>>([]);

        public Task<IReadOnlyList<Device>> SearchAsync(
            DeviceSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Device>>([]);

        public Task<DeviceLookupResult> LookupAsync(
            string identifier,
            DateOnly today,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceLookupResult { Device = null, Identifier = identifier });

        public Task<DeviceLookupResult> LookupExactAsync(
            string identifier,
            DateOnly today,
            CancellationToken cancellationToken) =>
            LookupAsync(identifier, today, cancellationToken);

        public Task<bool> HasReturnInProgressAsync(
            Guid deviceId,
            Guid? excludingRequestId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    /// <summary>Reached only when the tool finds a device, which the empty catalogue prevents.</summary>
    private sealed class UnusedEligibilityService : IEligibilityService
    {
        public RmaEligibilityDecision Evaluate(RmaPolicyRequest request) =>
            throw new InvalidOperationException("No device resolved, so policy must not be evaluated.");
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

        return new HttpContextCallerIdentity(new HttpContextAccessor { HttpContext = http }, "rma:support");
    }

    private static ClaimsPrincipal TokenWith(string scope) =>
        new(new ClaimsIdentity(
            [
                new Claim("sub", "agent-1"),
                new Claim("client_id", "rma-agent"),
                new Claim("scope", scope),
            ],
            authenticationType: "Bearer"));

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

    /// <summary>
    /// D3's grant is a scope, not a role name in a prose claim: the token either carries it or not,
    /// and the comparison is exact so `rma:support-preview` cannot pass for `rma:support`.
    /// </summary>
    [Fact]
    public void Only_a_token_that_carries_the_support_scope_may_unmask()
    {
        Assert.True(IdentityFor(TokenWith("rma:read rma:support")).IsSupportCaller);
        Assert.False(IdentityFor(TokenWith("rma:read")).IsSupportCaller);
        Assert.False(IdentityFor(TokenWith("rma:read rma:support-preview")).IsSupportCaller);
    }

    [Fact]
    public void A_stdio_session_never_may_unmask()
    {
        Assert.False(IdentityFor(user: null).IsSupportCaller);
    }

    /// <summary>
    /// A host built without a token source passes no scope, and then nobody is a support caller even
    /// if a request somehow arrives with a user: the gate must not fall back to "any authenticated
    /// caller" when the setting is missing.
    /// </summary>
    [Fact]
    public void An_identity_with_no_configured_scope_never_may_unmask()
    {
        var http = new DefaultHttpContext { User = TokenWith("rma:read rma:support") };

        var identity = new HttpContextCallerIdentity(
            new HttpContextAccessor { HttpContext = http },
            supportScope: null);

        Assert.False(identity.IsSupportCaller);
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

    /// <summary>
    /// The unmask tool checks this scope, and a blank one can never appear in a token's scope claim:
    /// the tool would be permanently refused while the deployment looked configured. A startup
    /// failure is the louder half of the same mistake.
    /// </summary>
    [Fact]
    public void An_empty_support_scope_is_refused()
    {
        var options = Usable();
        options.SupportScope = " ";

        var error = Assert.Throws<InvalidOperationException>(
            () => options.EnsureUsable(isDevelopment: false));

        Assert.Contains("SupportScope", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A paste carries padding and its own capitalisation, and neither keeps the grants apart.</summary>
    [Fact]
    public void A_support_scope_equal_apart_from_case_and_padding_is_refused()
    {
        var options = Usable();
        options.SupportScope = " RMA:Read ";

        var error = Assert.Throws<InvalidOperationException>(
            () => options.EnsureUsable(isDevelopment: false));

        Assert.Contains("SupportScope", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure this check exists for is a copy-paste, and it is the only precondition here that
    /// widens access rather than breaking the host: the deployment starts, the read tools work, and
    /// the grant that admits the one unmasking tool is the grant every caller already holds.
    /// </summary>
    [Fact]
    public void A_support_scope_equal_to_the_read_scope_is_refused()
    {
        var options = Usable();
        options.RequiredScope = "rma:read";
        options.SupportScope = "rma:read";

        var error = Assert.Throws<InvalidOperationException>(
            () => options.EnsureUsable(isDevelopment: false));

        Assert.Contains("SupportScope", error.Message, StringComparison.Ordinal);
        Assert.Contains("RequiredScope", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal must not catch the shape the committed realm ships: two different scopes are what
    /// every other test in this file builds on, so a check that refused them would fail there first.
    /// </summary>
    [Fact]
    public void Distinct_scopes_start_outside_development()
    {
        var options = Usable();
        Assert.Equal("rma:read", options.RequiredScope, StringComparer.Ordinal);
        Assert.Equal("rma:support", options.SupportScope, StringComparer.Ordinal);

        options.EnsureUsable(isDevelopment: false);
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
