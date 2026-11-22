using AIEnabledRma.Domain.Configuration;
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

    /// <summary>
    /// The message has always said "default or empty", and the rule used to exclude empty so a test
    /// host on the checked-in placeholder would still boot. A deployment with no password then
    /// started cleanly and failed at its first query, which is the failure the check exists to
    /// prevent. A placeholder fails the same way: it is present, non-empty, and authenticates
    /// against nothing.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("replace-with-a-long-random-password")]
    [InlineData("placeholder")]
    [InlineData("Replace-Me")]
    public void An_empty_or_placeholder_database_password_is_refused(string password)
    {
        var config = Config(("ConnectionStrings:Rma", $"Host=db;Database=x;Username=u;Password={password}"));

        Assert.Contains(ProductionConfiguration.Problems(config), p => p.Contains("password", StringComparison.Ordinal));
    }

    /// <summary>
    /// The bundled model is the right default for a checkout and the wrong model for a customer. A
    /// deployment that never chose a provider would look configured, answer every question and never
    /// ask a model; an absent setting is the same decision, because the provider switch treats blank
    /// as 'example-local'.
    /// </summary>
    [Theory]
    [InlineData("example-local")]
    [InlineData("Example-Local")]
    [InlineData("")]
    [InlineData(null)]
    public void The_bundled_example_model_is_refused(string? provider)
    {
        var config = Config(
            ("ConnectionStrings:Rma", SafeConnection),
            ("Ai:Provider", provider));

        Assert.Contains(
            ProductionConfiguration.Problems(config),
            p => p.Contains("Ai:Provider", StringComparison.Ordinal)
                && p.Contains("example-local", StringComparison.Ordinal));
    }

    [Fact]
    public void A_placeholder_operator_secret_is_refused()
    {
        var config = Config(
            ("ConnectionStrings:Rma", SafeConnection),
            ("Ai:Provider", "openai-compatible"),
            ("Ai:OpenAiCompatible:BaseUrl", "https://llm.example.com/v1"),
            ("OperatorAuth:Authority", "https://idp.example.com/realms/rma"),
            ("OperatorAuth:ClientId", "rma-operator"),
            ("OperatorAuth:ClientSecret", "replace-me"));

        Assert.Contains(
            ProductionConfiguration.Problems(config, checkOperatorSignIn: true),
            p => p.Contains("OperatorAuth:ClientSecret", StringComparison.Ordinal));
    }

    /// <summary>
    /// The example env file ships live-key placeholders. Compose only checks that a value is present,
    /// so without this rule a deployment copied from the example charges nothing and signs no
    /// webhook, and the first customer to pay finds out.
    /// </summary>
    [Fact]
    public void Placeholder_stripe_keys_are_refused()
    {
        var config = Config(
            ("ConnectionStrings:Rma", SafeConnection),
            ("Ai:Provider", "openai-compatible"),
            ("Ai:OpenAiCompatible:BaseUrl", "https://llm.example.com/v1"),
            ("Payments:Provider", "stripe"),
            ("Payments:Stripe:SecretKey", "sk_live_replace-me"),
            ("Payments:Stripe:WebhookSecret", "whsec_replace-me"));

        var problems = ProductionConfiguration.Problems(config);

        Assert.Contains(problems, p => p.Contains("Payments:Stripe:SecretKey", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Payments:Stripe:WebhookSecret", StringComparison.Ordinal));
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
    /// The production rule lives in the registration both transports call, so this asserts it
    /// there: registering the shared services in a Production host throws, before either transport
    /// exists. That is the property a hosted service could not give — it would move the same
    /// decision to StartAsync, where a transport that never starts would never check.
    /// </summary>
    [Fact]
    public void The_tool_server_refuses_a_development_password_in_production()
    {
        var builder = ToolServerBuilder("Production");

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.Services.AddRmaMcpServices(builder.Configuration, builder.Environment));

        Assert.Contains("unsafe configuration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("password", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same credentials in a Development host are accepted: the rule is about the environment the
    /// process runs in, not about the password on its own, and a rule that ignored the environment
    /// would refuse every developer's local run of the tool server.
    /// </summary>
    [Fact]
    public void The_tool_server_accepts_a_development_password_in_development()
    {
        var builder = ToolServerBuilder("Development");

        builder.Services.AddRmaMcpServices(builder.Configuration, builder.Environment);

        // Registered, not merely unrefused: the host both transports build is still constructible.
        using var host = builder.Build();

        Assert.NotNull(host);
    }

    /// <summary>
    /// Testing is not Production either, and the rule has to say so: an implementation that refused
    /// everywhere except Development would take a test or staging deployment down at startup.
    /// </summary>
    [Fact]
    public void The_tool_server_accepts_a_development_password_in_testing()
    {
        var builder = ToolServerBuilder("Testing");

        builder.Services.AddRmaMcpServices(builder.Configuration, builder.Environment);

        using var host = builder.Build();

        Assert.NotNull(host);
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

    /// <summary>
    /// The tool server calls no model, so the example-provider rule must not apply to it; a valid
    /// Production deployment with a safe database and no <c>Ai</c> section still registers.
    /// </summary>
    [Fact]
    public void The_tool_server_does_not_require_a_model_provider_in_production()
    {
        var builder = ToolServerBuilder("Production", SafeConnection);

        builder.Services.AddRmaMcpServices(builder.Configuration, builder.Environment);

        using var host = builder.Build();

        Assert.NotNull(host);
    }

    /// <summary>A tool-server builder with the environment under test and the development connection string.</summary>
    private static HostApplicationBuilder ToolServerBuilder(string environment, string connection = DevelopmentConnection)
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
            ["ConnectionStrings:Rma"] = connection,
        });

        return builder;
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
