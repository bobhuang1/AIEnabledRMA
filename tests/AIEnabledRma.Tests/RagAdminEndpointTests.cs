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

    [Fact]
    public void A_production_host_without_a_key_refuses_to_boot()
    {
        // The rule the deployment depends on: outside Development there is no loopback fallback,
        // because the documented deployment proxies to this host from the same machine. A host
        // that started keyless in Production would serve all three operator endpoints to anyone
        // who reached the proxy, so the entry point throws and nothing listens.
        using var factory = new RagFactory(adminKey: null, peer: Loopback, environment: "Production");

        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        // The test host wraps startup failures, so the assertion reads the whole exception chain
        // rather than the outermost type: what matters is that the process did not come up, and
        // that the failure names the setting that is missing.
        Assert.Contains(
            RagAdminGate.ApiKeyConfigurationKey,
            thrown.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_production_host_refuses_to_boot_when_the_corpus_has_load_errors()
    {
        // A key is configured so the key guard passes and the corpus rule is the one that fails.
        // Without that, this would go green on the other refusal and the new check would never run.
        using var corpus = new CorpusDirectory();
        var broken = corpus.Write("kb-9001-broken.md", "just some prose with no front matter");

        using var factory = new RagFactory(
            AdminKey,
            peer: Loopback,
            environment: "Production",
            knowledgeBasePath: corpus.Root);

        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        // The test host wraps startup failures, so the assertion reads the chain: what matters is
        // that the process did not come up, and that the failure names the article to fix.
        Assert.Contains("load error", thrown.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(broken, thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_production_host_refuses_to_boot_when_the_corpus_is_empty()
    {
        // A directory that exists and holds no articles produces its own load error but no
        // per-file reason, so this case is about the count rather than about any one file.
        using var corpus = new CorpusDirectory();
        Directory.CreateDirectory(corpus.Root);

        using var factory = new RagFactory(
            AdminKey,
            peer: Loopback,
            environment: "Production",
            knowledgeBasePath: corpus.Root);

        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("no articles were loaded", thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_development_host_boots_on_a_broken_corpus_so_the_error_can_be_inspected()
    {
        // The refusal is Development-only on purpose: on a developer's machine the useful behaviour
        // is to come up and name the file through /diagnostics, not to crash-loop with no output.
        var ct = TestContext.Current.CancellationToken;
        using var corpus = new CorpusDirectory();
        corpus.Write("kb-9001-broken.md", "just some prose with no front matter");

        using var factory = new RagFactory(adminKey: null, peer: Loopback, knowledgeBasePath: corpus.Root);
        using var client = factory.CreateClient();

        using var diagnostics = await GetJsonAsync(client, "/diagnostics", ct);
        Assert.Equal(0, diagnostics.RootElement.GetProperty("articleCount").GetInt32());
        Assert.NotEmpty(diagnostics.RootElement.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task A_reload_records_who_asked_and_what_it_did()
    {
        var ct = TestContext.Current.CancellationToken;
        var audit = new CapturingReloadAudit();

        using var factory = new RagFactory(AdminKey, peer: OffHost, reloadAudit: audit);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RagAdminGate.HeaderName, AdminKey);

        var response = await client.PostAsync("/rag/reload", content: null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The swap is the only operation that changes what the next customer is told, and it is a
        // shared credential that performs it: the record has to name the credential and the
        // address, because the key itself identifies nobody.
        var attempt = Assert.Single(audit.Attempts);
        Assert.True(attempt.Succeeded);
        Assert.Equal("admin-key", attempt.Caller);
        Assert.Equal(OffHost.ToString(), attempt.RemoteAddress);
        Assert.True(attempt.ArticleCountBefore > 0);
        Assert.True(attempt.ArticleCountAfter > 0);
        Assert.InRange(attempt.DurationMilliseconds, 0, 60_000);
    }

    [Fact]
    public async Task A_refused_reload_is_recorded_as_a_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var audit = new CapturingReloadAudit();

        using var corpus = new CorpusDirectory();
        corpus.Write("kb-9001-broken.md", "just some prose with no front matter");

        using var factory = new RagFactory(
            adminKey: null,
            peer: Loopback,
            knowledgeBasePath: corpus.Root,
            reloadAudit: audit);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/rag/reload", content: null, ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // A refusal is an attempt: without this row the only trace of a rejected deploy is a 422
        // in whatever happened to be watching, and the operator after a shift change has nothing.
        var attempt = Assert.Single(audit.Attempts);
        Assert.False(attempt.Succeeded);
        Assert.Equal("loopback", attempt.Caller);
        Assert.Equal(0, attempt.ArticleCountAfter);
    }

    [Fact]
    public async Task A_production_host_with_a_key_boots_and_keeps_the_endpoints_gated()
    {
        // With a key the host comes up, and the corpus is reachable through the gate. The gating
        // decisions themselves are settled in Development; what this case adds is that
        // Production's forced review requirement is satisfied by the articles as shipped, which
        // the article count proves and a refused corpus would not.
        var ct = TestContext.Current.CancellationToken;
        using var factory = new RagFactory(AdminKey, peer: Loopback, environment: "Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RagAdminGate.HeaderName, AdminKey);

        using var diagnostics = await GetJsonAsync(client, "/diagnostics", ct);
        Assert.True(diagnostics.RootElement.GetProperty("articleCount").GetInt32() > 0);
    }

    /// <summary>Collects what the host would have written to its log, so assertions are about records.</summary>
    private sealed class CapturingReloadAudit : IRagReloadAudit
    {
        public List<RagReloadAttempt> Attempts { get; } = [];

        public void Record(RagReloadAttempt attempt) => Attempts.Add(attempt);
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
    /// A corpus directory this test owns, so a broken article is never written into the shipped
    /// corpus and one case cannot leave state behind for another.
    /// </summary>
    private sealed class CorpusDirectory : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "rag-corpus-" + Guid.NewGuid().ToString("N"));

        public string Write(string fileName, string content)
        {
            Directory.CreateDirectory(Root);
            var file = System.IO.Path.Combine(Root, fileName);
            File.WriteAllText(file, content);
            return file;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Boots the RAG host with a stated caller address. TestServer reports no peer address, and
    /// the gate trusts no unknown caller, so every host has to say where its requests come from.
    /// The knowledge-base path is made absolute by walking up from the test binary, because the
    /// host resolves a relative path against the working directory, which differs between a
    /// developer's shell, CI, and this suite.
    /// </summary>
    private sealed class RagFactory(
        string? adminKey,
        IPAddress peer,
        string environment = "Development",
        string? knowledgeBasePath = null,
        IRagReloadAudit? reloadAudit = null)
        : WebApplicationFactory<global::RagAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Rag:KnowledgeBasePath", knowledgeBasePath ?? KnowledgeBasePath());

            if (adminKey is not null)
            {
                builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, adminKey);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new PeerAddressStartupFilter(peer));

                if (reloadAudit is not null)
                {
                    services.AddSingleton(reloadAudit);
                }
            });
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
