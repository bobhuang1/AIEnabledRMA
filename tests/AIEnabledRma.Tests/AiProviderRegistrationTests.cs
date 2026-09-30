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
    [InlineData("/v1/chat")]
    [InlineData("api.example.com/v1")]
    public void A_relative_or_malformed_base_url_fails_at_startup(string baseUrl)
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
