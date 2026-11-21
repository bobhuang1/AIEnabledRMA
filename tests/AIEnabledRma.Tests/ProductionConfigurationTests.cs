using AIEnabledRma.Data;
using AIEnabledRma.Mcp;
using AIEnabledRma.Rag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AIEnabledRma.Tests;

/// <summary>P11: Production refuses an unsafe configuration at startup. All values are synthetic.</summary>
public sealed class ProductionConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private const string SafeConnection = "Host=db;Database=rmaprod;Username=rma_app;Password=Very-Long-Synthetic-Password-123";

    private const string DevelopmentConnection = "Host=localhost;Database=rmadev;Username=rmauser;Password=rmapassword";

    [Fact]
    public void A_safe_configuration_has_no_problems()
    {
        var config = Config(
            ("ConnectionStrings:Rma", SafeConnection),
            ("Ai:Provider", "openai-compatible"),
            ("Ai:OpenAiCompatible:BaseUrl", "https://llm.example.com/v1"),
            ("OperatorAuth:Authority", "https://idp.example.com/realms/rma"),
            ("OperatorAuth:ClientId", "rma-operator"));

        Assert.Empty(ProductionConfiguration.Problems(config, checkOperatorSignIn: true));
    }

    [Theory]
    [InlineData("rmapassword")]
    [InlineData("postgres")]
    [InlineData("testonly")]
    public void A_default_database_password_is_refused(string password)
    {
        var config = Config(("ConnectionStrings:Rma", $"Host=db;Database=x;Username=u;Password={password}"));

        Assert.Contains(ProductionConfiguration.Problems(config), p => p.Contains("password", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plain_http_model_endpoint_is_refused()
    {
        var config = Config(
            ("ConnectionStrings:Rma", SafeConnection),
            ("Ai:Provider", "openai-compatible"),
            ("Ai:OpenAiCompatible:BaseUrl", "http://llm.example.com/v1"),
            ("Embeddings:Provider", "openai-compatible"),
            ("Embeddings:BaseUrl", "http://emb.example.com/v1"));

        var problems = ProductionConfiguration.Problems(config);

        Assert.Contains(problems, p => p.StartsWith("Ai:OpenAiCompatible:BaseUrl", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Embeddings:BaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_operator_sign_in_is_refused_when_asked_for()
    {
        var problems = ProductionConfiguration.Problems(Config(("ConnectionStrings:Rma", SafeConnection)), checkOperatorSignIn: true);

        Assert.Contains(problems, p => p.Contains("OperatorAuth", StringComparison.Ordinal));
    }

    [Fact]
    public void The_web_host_refuses_production_with_a_development_password_and_a_plain_http_model()
    {
        using var factory = new Factory<global::WebAppAnchor>(
            "Production",
            ("ConnectionStrings:Rma", "Host=localhost;Database=rmadev;Username=rmauser;Password=rmapassword"),
            ("Ai:Provider", "openai-compatible"),
            ("Ai:OpenAiCompatible:BaseUrl", "http://llm.example.com/v1"),
            ("Payments:Fake:AllowInProduction", "true"),
            ("Rag:Admin:ApiKey", "production-config-test-key"));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("unsafe configuration", ex.ToString(), StringComparison.Ordinal);
        Assert.Contains("password", ex.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The tool server's check is registered with the services both transports share, so this
    /// registers those services in a host and starts it. Calling the check directly would prove it
    /// works and say nothing about whether anything runs it on the stdio transport.
    /// </summary>
    [Fact]
    public async Task The_tool_server_refuses_a_development_password_in_production()
    {
        using var host = ToolServerHost("Production");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("unsafe configuration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("password", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same credentials in a Testing host are accepted: the rule is about the environment the
    /// process runs in, not about the password on its own, and a check that ignored the environment
    /// would break every test host in this repository.
    /// </summary>
    [Fact]
    public async Task The_tool_server_accepts_a_development_password_in_testing()
    {
        using var host = ToolServerHost("Testing");

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The RAG host applies the same rule from its entry point. It boots the real host rather than a
    /// test double, because the check sits in the top-level statements where a direct test cannot
    /// reach it.
    /// </summary>
    [Fact]
    public void The_rag_host_refuses_a_development_password_in_production()
    {
        using var factory = new RagHostFactory("Production");

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("unsafe configuration", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("password", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rag_host_accepts_a_development_password_in_testing()
    {
        using var factory = new RagHostFactory("Testing");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/health",
            TestContext.Current.CancellationToken);

        // A host that came up answers its liveness probe; a refused configuration never listens.
        Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>A tool-server host built from the shared services and started, as both transports start it.</summary>
    private static IHost ToolServerHost(string environment)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Added after the host's own sources, so the committed appsettings cannot mask the value the
        // case is about.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Rma"] = DevelopmentConnection,
        });

        builder.Services.AddRmaMcpServices(builder.Configuration);

        return builder.Build();
    }

    /// <summary>
    /// The real RAG entry point on the corpus the checkout ships, configured with the development
    /// connection string: what the case is about is what the host does with it.
    /// </summary>
    private sealed class RagHostFactory(string environment) : WebApplicationFactory<global::RagAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Rma", DevelopmentConnection);
            builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, "production-config-test-key");
            builder.UseSetting("Rag:KnowledgeBasePath", KnowledgeBasePath());
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

    private sealed class Factory<T>(string environment, params (string Key, string Value)[] settings) : WebApplicationFactory<T>
        where T : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }
}
