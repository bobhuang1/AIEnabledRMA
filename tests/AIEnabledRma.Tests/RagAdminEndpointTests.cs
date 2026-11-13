using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AIEnabledRma.Rag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AIEnabledRma.Tests;

/// <summary>
/// Boots the RAG host and knocks on the endpoints it exposes. The gate's individual decisions
/// are unit tested in <see cref="RagAdminGateTests"/>; these cases pin the wiring and each trust
/// mode end to end — that all three operational endpoints sit behind the gate, that a caller
/// from another machine is refused whether or not a key is configured, that the configured key
/// is what admits them, and that with no key only loopback gets in.
/// </summary>
public sealed class RagAdminEndpointTests
{
    private const string AdminKey = "test-admin-key";

    private static readonly IPAddress Loopback = IPAddress.Loopback;
    private static readonly IPAddress OffHost = IPAddress.Parse("203.0.113.7");

    [Fact]
    public async Task With_no_key_configured_the_operational_endpoints_are_loopback_only()
    {
        var ct = TestContext.Current.CancellationToken;

        // `dotnet run` on a developer's own machine: same host, no key, everything works.
        using (var localHost = new RagFactory(adminKey: null, peer: Loopback))
        using (var local = localHost.CreateClient())
        {
            Assert.Equal(HttpStatusCode.OK, (await local.GetAsync("/diagnostics", ct)).StatusCode);
        }

        // The same host without a key, called from another machine: refused. An unprotected RAG
        // port is not a corpus leak to whatever network it happens to sit on.
        using (var remoteHost = new RagFactory(adminKey: null, peer: OffHost))
        using (var remote = remoteHost.CreateClient())
        {
            await AssertAllThreeRefusedAsync(remote, ct);
        }
    }

    [Fact]
    public async Task With_a_key_configured_an_off_host_caller_is_refused_without_the_right_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new RagFactory(AdminKey, peer: OffHost);
        using var client = factory.CreateClient();

        await AssertAllThreeRefusedAsync(client, ct);

        // 404 rather than 401 or 403: the endpoints are not advertised, so a caller who was never
        // told they exist should not learn from the response that they do.
        client.DefaultRequestHeaders.Add(RagAdminGate.HeaderName, AdminKey + "-wrong");
        await AssertAllThreeRefusedAsync(client, ct);
    }

    [Fact]
    public async Task With_a_key_configured_an_off_host_caller_holding_it_reaches_all_three()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new RagFactory(AdminKey, peer: OffHost);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RagAdminGate.HeaderName, AdminKey);

        // Parsed rather than substring-matched, so the assertion is about what the endpoint
        // reports — the corpus it is actually serving — and not about JSON formatting.
        using var diagnostics = await GetJsonAsync(client, "/diagnostics", ct);
        Assert.True(diagnostics.RootElement.GetProperty("articleCount").GetInt32() > 0);

        using var retrieve = await PostJsonAsync(
            client,
            "/retrieve",
            new { query = "screen stays black" },
            ct);
        Assert.True(retrieve.RootElement.GetProperty("count").GetInt32() > 0);

        // Reload reports success only when the corpus it read had no load errors, so a 200 here
        // also proves the knowledge-base path the factory configured was readable.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/rag/reload", content: null, ct)).StatusCode);
    }

    [Fact]
    public async Task The_rag_health_endpoint_stays_public_and_reports_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;

        // The most hostile configuration available: a key is configured and the caller is not on
        // this machine. The liveness probe still answers, because a probe that needs the key is
        // a probe that reports "down" whenever the key is wrong.
        using var factory = new RagFactory(AdminKey, peer: OffHost);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Exactly one property, and it says nothing about the corpus. Structural rather than an
        // exact string, so serialiser formatting is not what the test rests on, while a field
        // added later still fails it.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var property = Assert.Single(body.RootElement.EnumerateObject());

        Assert.Equal("status", property.Name);
        Assert.Equal("ok", property.Value.GetString());
    }

    /// <summary>All three operational endpoints, so no case can accidentally cover only two.</summary>
    private static async Task AssertAllThreeRefusedAsync(HttpClient client, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/diagnostics", ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/retrieve", new { query = "screen stays black" }, ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync("/rag/reload", content: null, ct)).StatusCode);
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<JsonDocument> PostJsonAsync(
        HttpClient client,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync(path, body, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// Boots the RAG host with a stated caller address. TestServer reports no peer address, and
    /// the gate trusts no unknown caller, so every host has to say where its requests come from.
    /// The knowledge-base path is made absolute by walking up from the test binary, because the
    /// host resolves a relative path against the working directory, which differs between a
    /// developer's shell, CI, and this suite.
    /// </summary>
    private sealed class RagFactory(string? adminKey, IPAddress peer)
        : WebApplicationFactory<global::RagAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Rag:KnowledgeBasePath", KnowledgeBasePath());

            if (adminKey is not null)
            {
                builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, adminKey);
            }

            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new PeerAddressStartupFilter(peer)));
        }

        private static string KnowledgeBasePath()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null
                   && !Directory.Exists(Path.Combine(directory.FullName, "knowledge-base")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(
                directory?.FullName ?? Directory.GetCurrentDirectory(),
                "knowledge-base");
        }
    }
}
