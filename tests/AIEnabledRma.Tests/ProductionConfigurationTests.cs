using AIEnabledRma.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AIEnabledRma.Tests;

/// <summary>P11: Production refuses an unsafe configuration at startup. All values are synthetic.</summary>
public sealed class ProductionConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private const string SafeConnection = "Host=db;Database=rmaprod;Username=rma_app;Password=Very-Long-Synthetic-Password-123";

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
    [InlineData("placeholder")]
    [InlineData("")]
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
    public void The_fake_gateway_opt_in_and_a_missing_operator_sign_in_are_refused()
    {
        var config = Config(("ConnectionStrings:Rma", SafeConnection), ("Payments:AllowFakeGateway", "true"));

        var problems = ProductionConfiguration.Problems(config, checkOperatorSignIn: true);

        Assert.Contains(problems, p => p.Contains("AllowFakeGateway", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("OperatorAuth", StringComparison.Ordinal));
    }

    [Fact]
    public void The_web_host_refuses_production_with_development_defaults()
    {
        using var factory = new Factory<global::WebAppAnchor>(
            "Production",
            ("ConnectionStrings:Rma", "Host=localhost;Database=rmadev;Username=rmauser;Password=rmapassword"),
            ("Payments:AllowFakeGateway", "true"));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("unsafe configuration", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_rag_host_refuses_production_without_keys()
    {
        using var factory = new Factory<global::RagAppAnchor>("Production");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("AdminKey", ex.ToString(), StringComparison.Ordinal);
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
