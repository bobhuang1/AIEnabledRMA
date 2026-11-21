using AIEnabledRma.Ai;
using AIEnabledRma.Domain.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// Provider selection and its settings are a deployment-time decision, so a wrong one should
/// stop the process at startup. Discovering it on the first customer question instead turns a
/// configuration mistake into what looks like an outage, and the triage fallback quietly
/// routes every case to a human while the logs say the model is fine.
/// </summary>
public sealed class AiProviderRegistrationTests
{
    private static ServiceProvider BuildProvider(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiProvider(configuration);
        return services.BuildServiceProvider();
    }

    // Resolving .Value is what runs the validators, so it stands in for the startup check
    // without needing to spin up a host.
    private static void Validate(IServiceProvider services) =>
        _ = services.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value;

    private static OptionsValidationException Failure(Action action) =>
        Assert.Throws<OptionsValidationException>(action);

    // ---------- required settings ----------

    [Fact]
    public void The_offline_provider_needs_no_configuration()
    {
        // The default deployment must start with nothing configured. A validation rule that
        // demanded provider settings unconditionally would break the quickstart path.
        using var services = BuildProvider();

        Validate(services);

        Assert.Equal(
            AiServiceCollectionExtensions.ExampleLocalProviderName,
            services.GetRequiredService<IChatModel>().ProviderName);
    }

    [Fact]
    public void The_offline_provider_does_not_care_about_a_blank_base_url()
    {
        using var services = BuildProvider(
            ("Ai:Provider", AiServiceCollectionExtensions.ExampleLocalProviderName),
            ("Ai:OpenAiCompatible:BaseUrl", ""));

        Validate(services);
    }

    [Fact]
    public void A_compatible_provider_without_a_base_url_fails_at_startup()
    {
        using var services = BuildProvider(
            ("Ai:Provider", AiServiceCollectionExtensions.OpenAiCompatibleProviderName));

        var error = Failure(() => Validate(services));

        Assert.Contains("BaseUrl", error.Failures.Single());
    }

    [Fact]
    public void A_compatible_provider_with_a_blank_base_url_fails_at_startup()
    {
        using var services = BuildProvider(
            ("Ai:Provider", AiServiceCollectionExtensions.OpenAiCompatibleProviderName),
            ("Ai:OpenAiCompatible:BaseUrl", "   "));

        var error = Failure(() => Validate(services));

        Assert.Contains("BaseUrl", error.Failures.Single());
    }

    [Theory]
    [InlineData("not a url")]

    // On Linux "/v1/chat" parses as the absolute URI file:///v1/chat, and "//api.example.com/v1"
    // parses as file://api.example.com/v1 with a real host. Uri.TryCreate on its own accepted
    // both, so the misconfiguration surfaced as a failed request instead of a refusal.
    [InlineData("/v1/chat")]
    [InlineData("//api.example.com/v1")]
    [InlineData("api.example.com/v1")]

    // Absolute in syntax but lacking an authority, so there is nothing to connect to.
    [InlineData("http://")]

    // Absolute, but not something HttpClient will talk to. The scheme is the rule, not just
    // absoluteness.
    [InlineData("ftp://api.example.com/v1")]
    [InlineData("file:///srv/chat")]
    public void A_relative_malformed_or_non_http_base_url_fails_at_startup(string baseUrl)
    {
        using var services = BuildProvider(
            ("Ai:Provider", AiServiceCollectionExtensions.OpenAiCompatibleProviderName),
            ("Ai:OpenAiCompatible:BaseUrl", baseUrl));

        Assert.Contains("absolute URL", Failure(() => Validate(services)).Failures.Single());
    }

    [Theory]
    [InlineData("https://api.example.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("  https://api.example.com/v1  ")]
    public void A_valid_base_url_starts_cleanly(string baseUrl)
    {
        using var services = BuildProvider(
            ("Ai:Provider", AiServiceCollectionExtensions.OpenAiCompatibleProviderName),
            ("Ai:OpenAiCompatible:BaseUrl", baseUrl));

        Validate(services);

        // Surrounding whitespace is a normal artefact of environment variables, and the model
        // trims before building the request URL, so binding must tolerate it.
        Assert.Equal(
            baseUrl.Trim(),
            services.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value.BaseUrl.Trim());
    }

    [Fact]
    public void A_compatible_provider_does_not_require_an_api_key()
    {
        // Ollama, LM Studio, and llama.cpp run without one, and they are the deployments a new
        // evaluator is most likely to point at first.
        using var services = BuildProvider(
            ("Ai:Provider", AiServiceCollectionExtensions.OpenAiCompatibleProviderName),
            ("Ai:OpenAiCompatible:BaseUrl", "http://localhost:11434/v1"));

        Validate(services);

        Assert.Null(services.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value.ApiKey);
    }

    [Fact]
    public void A_non_positive_timeout_fails_at_startup()
    {
        using var services = BuildProvider(
            ("Ai:Provider", AiServiceCollectionExtensions.OpenAiCompatibleProviderName),
            ("Ai:OpenAiCompatible:BaseUrl", "https://api.example.com/v1"),
            ("Ai:OpenAiCompatible:TimeoutSeconds", "0"));

        Assert.Contains("TimeoutSeconds", Failure(() => Validate(services)).Failures.Single());
    }

    // ---------- payment gateway ----------

    [Fact]
    public void The_fake_payment_gateway_refuses_to_register_outside_development()
    {
        // The bundled gateway accepts every positive amount. Registering it in production would
        // approve every charge while still looking like a working payment step, so the composition
        // root has to fail at startup rather than ship one.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddFakePaymentGateway(configuration, isDevelopment: false));

        Assert.Contains("Development", error.Message);
        Assert.Contains($"{FakePaymentGatewayOptions.SectionName}:AllowInProduction", error.Message);
    }

    [Fact]
    public void The_fake_payment_gateway_registers_in_development_or_when_explicitly_allowed()
    {
        var development = new ServiceCollection();
        development.AddLogging();
        development.AddFakePaymentGateway(new ConfigurationBuilder().Build(), isDevelopment: true);

        using (var provider = development.BuildServiceProvider())
        {
            // Qualified: the test project has its own FakePaymentGateway double, and this asserts
            // the production one was registered.
            Assert.IsType<AIEnabledRma.Ai.FakePaymentGateway>(
                provider.GetRequiredService<IPaymentGateway>());
        }

        // The escape hatch is for a deployment that genuinely wants the demo gateway, and has to
        // say so by name rather than inherit it from a missing setting.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{FakePaymentGatewayOptions.SectionName}:AllowInProduction"] = "true",
            })
            .Build();

        var explicitlyAllowed = new ServiceCollection();
        explicitlyAllowed.AddLogging();
        explicitlyAllowed.AddFakePaymentGateway(configuration, isDevelopment: false);

        using var allowed = explicitlyAllowed.BuildServiceProvider();
        Assert.IsType<AIEnabledRma.Ai.FakePaymentGateway>(
            allowed.GetRequiredService<IPaymentGateway>());
    }

    // ---------- provider selection ----------

    [Fact]
    public void An_unknown_provider_is_rejected_with_the_supported_values_named()
    {
        // A typo must not silently fall back to the offline model: that would look healthy and
        // quietly answer customers from the bundled example responses.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai-compatible-ish",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddAiProvider(configuration));

        Assert.Contains(AiServiceCollectionExtensions.ExampleLocalProviderName, error.Message);
        Assert.Contains(AiServiceCollectionExtensions.OpenAiCompatibleProviderName, error.Message);
    }

    [Fact]
    public void A_blank_provider_falls_back_to_the_offline_model()
    {
        using var services = BuildProvider(("Ai:Provider", "  "));

        Validate(services);

        Assert.Equal(
            AiServiceCollectionExtensions.ExampleLocalProviderName,
            services.GetRequiredService<IChatModel>().ProviderName);
    }

    [Fact]
    public void The_reported_provider_matches_the_one_that_was_configured()
    {
        using var services = BuildProvider(("Ai:Provider", "example-local"));

        var info = services.GetRequiredService<ProviderInfo>();

        // The diagnostics endpoint exists so an operator can confirm the real provider without
        // reading configuration, so the two must not drift.
        Assert.Equal("example-local", info.ConfiguredProvider);
        Assert.Equal(services.GetRequiredService<IChatModel>().ProviderName, info.ResolvedProvider);
        Assert.NotEmpty(info.Notes);
    }
}
