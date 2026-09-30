using AIEnabledRma.Ai;
using AIEnabledRma.Domain.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Ai;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AI provider named by <c>Ai:Provider</c>. This is the single switch that
    /// decides which model the process talks to.
    ///
    /// Supported values:
    ///   <c>example-local</c>       bundled, offline, deterministic (default)
    ///   <c>openai-compatible</c>   any /chat/completions endpoint
    ///
    /// Adding another provider means adding one <see cref="IChatModel"/> implementation and
    /// one more <c>case</c> here. Nothing else in the solution changes.
    /// </summary>
    public static IServiceCollection AddAiProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ExampleModelOptions>()
            .Bind(configuration.GetSection(ExampleModelOptions.SectionName))
            .ValidateOnStart();

        var provider = ResolveProvider(configuration);

        // A provider's settings are only required when that provider is the one selected, and
        // they are checked at startup rather than on the first customer question. The old rule
        // only rejected a *non-blank* unparseable URL, so 'openai-compatible' with no BaseUrl
        // started cleanly and then failed every request, which read as an outage rather than
        // as a misconfiguration.
        var needsBaseUrl = string.Equals(provider, OpenAiCompatibleProviderName, StringComparison.Ordinal);

        services.AddOptions<OpenAiCompatibleOptions>()
            .Bind(configuration.GetSection(OpenAiCompatibleOptions.SectionName))
            .Validate(
                o => !needsBaseUrl || !string.IsNullOrWhiteSpace(o.BaseUrl),
                "Ai:OpenAiCompatible:BaseUrl is required when Ai:Provider is '"
                + OpenAiCompatibleProviderName + "'.")
            .Validate(
                o => !needsBaseUrl
                     || string.IsNullOrWhiteSpace(o.BaseUrl)
                     || Uri.TryCreate(o.BaseUrl.Trim(), UriKind.Absolute, out _),
                "Ai:OpenAiCompatible:BaseUrl must be an absolute URL, for example "
                + "'https://api.example.com/v1' or 'http://localhost:11434/v1'.")
            .Validate(
                o => o.TimeoutSeconds > 0,
                "Ai:OpenAiCompatible:TimeoutSeconds must be greater than zero.")
            .ValidateOnStart();

        // An API key is deliberately not required: the local servers this adapter is documented
        // against (Ollama, LM Studio, llama.cpp) run without one, and demanding it would break
        // exactly the deployments that are easiest to try.

        switch (provider)
        {
            case ExampleLocalProviderName:
                services.TryAddSingleton<IChatModel, ExampleLocalChatModel>();
                break;

            case OpenAiCompatibleProviderName:
                services.AddHttpClient<IChatModel, OpenAiCompatibleChatModel>((sp, http) =>
                {
                    var options = sp.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value;
                    http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                });
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown Ai:Provider '{provider}'. Supported values are "
                    + $"'{ExampleLocalProviderName}' and '{OpenAiCompatibleProviderName}'. "
                    + "To add a provider, register an IChatModel implementation for it.");
        }

        services.AddSingleton<ProviderInfo>(sp => new ProviderInfo(
            provider,
            sp.GetRequiredService<IChatModel>().ProviderName,
            BuildDiagnosticNotes(provider)));

        return services;
    }

    public const string ExampleLocalProviderName = "example-local";
    public const string OpenAiCompatibleProviderName = "openai-compatible";

    /// <summary>
    /// Resolves the configured provider name, defaulting to the bundled offline model. Shared
    /// by the registration switch and the option validators so the two can never disagree
    /// about which provider is in force.
    /// </summary>
    private static string ResolveProvider(IConfiguration configuration)
    {
        var configured = configuration["Ai:Provider"];

        return string.IsNullOrWhiteSpace(configured) ? ExampleLocalProviderName : configured.Trim();
    }

    private static IReadOnlyList<string> BuildDiagnosticNotes(string provider) => provider switch
    {
        ExampleLocalProviderName =>
        [
            "Offline deterministic example model. No network calls and no API key.",
            "It cites only the articles supplied by the RAG service, which are independently "
            + "verified by the triage pipeline.",
            "Replace it with a real provider before using this for an actual returns process.",
        ],

        OpenAiCompatibleProviderName =>
        [
            "OpenAI-compatible /chat/completions endpoint.",
            "Model output is treated as untrusted: schema validation, citation verification, "
            + "and the fail-closed fallback all run regardless of what the model returns.",
        ],

        _ => ["Unrecognised provider. No behavioural guarantees can be stated."],
    };
}

/// <summary>
/// Reported by the <c>/diagnostics</c> endpoint so an operator can confirm at runtime which
/// model is actually in use, without reading configuration or source.
/// </summary>
public sealed record ProviderInfo(
    string ConfiguredProvider,
    string ResolvedProvider,
    IReadOnlyList<string> Notes);
