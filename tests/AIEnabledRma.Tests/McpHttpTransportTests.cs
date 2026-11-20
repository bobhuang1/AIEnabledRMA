using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AIEnabledRma.Tests;

/// <summary>
/// P2/D2: the remote MCP transport. Every request needs a valid token for this server's audience,
/// with the required scope, from an allowed client, and no disallowed browser Origin. A valid
/// caller reaches the same guarded tools as stdio, and its identity lands in the audit trail.
/// All keys, issuers and client ids are synthetic.
/// </summary>
public sealed class McpHttpTransportTests : IAsyncLifetime
{
    private const string Issuer = "https://idp.test/realms/rma";
    private const string Audience = "aienabledrma-mcp";
    private const string AllowedClient = "support-agent";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("synthetic-test-signing-key-0123456789abcdef-not-a-secret"));

    private readonly McpSecurityTests.RecordingAuditSink _audit = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration["ConnectionStrings:Rma"] = "Host=localhost;Database=unused;Username=x;Password=placeholder";
        builder.Configuration["McpHttp:Authority"] = Issuer;
        builder.Configuration["McpHttp:Audience"] = Audience;
        builder.Configuration["McpHttp:AllowedClientIds:0"] = AllowedClient;
        builder.Configuration["McpHttp:AllowedOrigins:0"] = "https://console.example.com";
        builder.Configuration["McpHttp:ResourceUrl"] = "https://mcp.example.com/mcp";

        McpHttpHost.Configure(builder);

        // Validate against the synthetic key instead of fetching the issuer's discovery document.
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.Authority = null;
            o.ConfigurationManager = null;
            o.TokenValidationParameters.ValidIssuer = Issuer;
            o.TokenValidationParameters.IssuerSigningKey = SigningKey;
        });

        var customers = new FakeCustomerRepository();
        customers.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane.smith@example.com",
            PhoneNumber = "+1 555 010 4477",
        });
        builder.Services.RemoveAll<ICustomerRepository>();
        builder.Services.AddScoped<ICustomerRepository>(_ => customers);
        builder.Services.RemoveAll<IRmaRepository>();
        builder.Services.AddScoped<IRmaRepository, FakeRmaRepository>();
        builder.Services.RemoveAll<IToolAuditSink>();
        builder.Services.AddSingleton<IToolAuditSink>(_audit);

        _app = builder.Build();
        McpHttpHost.Map(_app);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static string Token(
        string audience = Audience,
        string scope = "rma:read",
        string clientId = AllowedClient,
        string issuer = Issuer)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "user-42",
            ["azp"] = clientId,
            ["scope"] = scope,
        };

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    private Task<HttpResponseMessage> CallAsync(string? token, string? origin = null, string method = "tools/list", string paramsJson = "{}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{paramsJson}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_token_is_401_with_a_pointer_to_the_resource_metadata()
    {
        var response = await CallAsync(token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_protected_resource_metadata_names_the_issuer_and_scope()
    {
        var body = await _client.GetStringAsync("/.well-known/oauth-protected-resource", TestContext.Current.CancellationToken);

        Assert.Contains(Issuer, body, StringComparison.Ordinal);
        Assert.Contains("rma:read", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(Token(audience: "some-other-api"))).StatusCode);

    [Fact]
    public async Task A_token_from_another_issuer_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(Token(issuer: "https://evil.test"))).StatusCode);

    [Fact]
    public async Task A_token_without_the_scope_is_403() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(Token(scope: "profile email"))).StatusCode);

    [Fact]
    public async Task A_client_that_is_not_allowed_is_403() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(Token(clientId: "random-app"))).StatusCode);

    [Fact]
    public async Task A_disallowed_browser_origin_is_403_even_with_a_valid_token() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(Token(), origin: "https://evil.example")).StatusCode);

    [Fact]
    public async Task A_valid_token_reaches_the_tools_and_the_call_is_audited_under_the_client()
    {
        var list = await CallAsync(Token(), origin: "https://console.example.com");
        var listBody = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("find_customer", listBody, StringComparison.Ordinal);

        var call = await CallAsync(
            Token(),
            method: "tools/call",
            paramsJson: """{"name":"find_customer","arguments":{"email":"jane.smith@example.com"}}""");
        var callBody = await call.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Contains("Jane S.", callBody, StringComparison.Ordinal);
        Assert.DoesNotContain("jane.smith@example.com", callBody, StringComparison.Ordinal);

        var row = Assert.Single(_audit.Calls);
        Assert.Equal("find_customer", row.Tool);
        Assert.Equal(AllowedClient, row.Caller);
    }

    [Fact]
    public async Task An_http_caller_with_the_support_role_can_reveal_but_a_plain_reader_cannot()
    {
        var args = """{"name":"reveal_customer_contact","arguments":{"customerId":"00000000-0000-0000-0000-000000000001","reason":"callback about the swap"}}""";

        var reader = await (await CallAsync(Token(), method: "tools/call", paramsJson: args)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var support = await (await CallAsync(Token(scope: "rma:read rma:support"), method: "tools/call", paramsJson: args)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("support", reader, StringComparison.Ordinal);
        Assert.Contains("\"isError\":true", reader.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Contains("notFound", support, StringComparison.Ordinal);
        Assert.Equal(["RefusedPermission", "Ok"], _audit.Calls.Select(c => c.Outcome).ToArray());
    }
}
