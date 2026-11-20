using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AIEnabledRma.Domain.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Ai;

/// <summary>
/// Embedding model selection (<c>Embeddings</c> section, decision D4). Switching model or provider is a
/// configuration change; the knowledge store records the model it was built with and refuses a
/// different one until <c>DbAdmin kb-ingest --rebuild</c> re-embeds everything.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    public const string Ollama = "ollama";
    public const string OpenAiCompatible = "openai-compatible";
    public const string Hashing = "hashing";

    /// <summary>
    /// <c>ollama</c> (default: a local model, so article and case text never leave the company),
    /// <c>openai-compatible</c> (any hosted or self-hosted <c>/embeddings</c> endpoint), or
    /// <c>hashing</c> (deterministic and offline, for tests and CI).
    /// </summary>
    public string Provider { get; set; } = Ollama;

    /// <summary>Ollama: its root URL (default http://localhost:11434). OpenAI-compatible: the /v1 base URL.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Model name as the provider knows it, e.g. <c>nomic-embed-text</c> or <c>bge-m3</c>.</summary>
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>Vector size; must match the model (nomic-embed-text 768, bge-m3 1024). Other sizes are refused.</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>Optional bearer key, from the environment or a secret store only.</summary>
    public string? ApiKey { get; set; }

    public int BatchSize { get; set; } = 32;

    public int MaxAttempts { get; set; } = 3;

    public int TimeoutSeconds { get; set; } = 60;

    public string NormalisedProvider => (Provider ?? string.Empty).Trim().ToLowerInvariant();

    public string EffectiveBaseUrl =>
        !string.IsNullOrWhiteSpace(BaseUrl)
            ? BaseUrl
            : NormalisedProvider == Ollama ? "http://localhost:11434" : string.Empty;
}

/// <summary>
/// Calls an OpenAI-compatible <c>/embeddings</c> endpoint in batches, retrying transient
/// failures with backoff. A batch that still fails throws, so the caller can mark those chunks
/// as failed rather than store them without vectors.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingModel(
    HttpClient http,
    IOptions<EmbeddingOptions> options,
    ILogger<OpenAiCompatibleEmbeddingModel> logger) : IEmbeddingModel
{
    private readonly EmbeddingOptions _options = options.Value;

    public string Name => $"openai-compatible:{_options.Model}";

    public int Dimensions => _options.Dimensions;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var results = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(Math.Max(1, _options.BatchSize)))
        {
            results.AddRange(await EmbeddingRetry.RunAsync(
                () => EmbedBatchAsync(batch, cancellationToken), _options.MaxAttempts, batch.Length, logger, cancellationToken));
        }

        return results;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(string[] batch, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "embeddings")
        {
            Content = JsonContent.Create(new EmbeddingRequest(_options.Model, batch)),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken)
            ?? throw new InvalidOperationException("The embedding endpoint returned an empty body.");

        return EmbeddingRetry.Checked(body.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList(), batch.Length, _options);
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<EmbeddingItem> Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}

/// <summary>
/// A local Ollama server through its native <c>/api/embed</c> endpoint, batched and retried (D4
/// default). Pull the model first, e.g. <c>ollama pull nomic-embed-text</c>.
/// </summary>
public sealed class OllamaEmbeddingModel(
    HttpClient http,
    IOptions<EmbeddingOptions> options,
    ILogger<OllamaEmbeddingModel> logger) : IEmbeddingModel
{
    private readonly EmbeddingOptions _options = options.Value;

    public string Name => $"ollama:{_options.Model}";

    public int Dimensions => _options.Dimensions;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var results = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(Math.Max(1, _options.BatchSize)))
        {
            results.AddRange(await EmbeddingRetry.RunAsync(
                () => EmbedBatchAsync(batch, cancellationToken), _options.MaxAttempts, batch.Length, logger, cancellationToken));
        }

        return results;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(string[] batch, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/embed")
        {
            Content = JsonContent.Create(new OllamaRequest(_options.Model, batch)),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Ollama returned an empty body.");

        return EmbeddingRetry.Checked(body.Embeddings, batch.Length, _options);
    }

    private sealed record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input);

    private sealed record OllamaResponse(
        [property: JsonPropertyName("embeddings")] IReadOnlyList<float[]> Embeddings);
}

internal static class EmbeddingRetry
{
    public static async Task<IReadOnlyList<float[]>> RunAsync(
        Func<Task<IReadOnlyList<float[]>>> call,
        int maxAttempts,
        int count,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (attempt < maxAttempts
                && ex is HttpRequestException or TaskCanceledException
                && !cancellationToken.IsCancellationRequested)
            {
                // Counts only: chunk text is never logged.
                logger.LogWarning(ex, "Embedding batch of {Count} failed (attempt {Attempt}); retrying.", count, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken);
            }
        }
    }

    /// <summary>One vector per input, each of the configured size; anything else is refused.</summary>
    public static IReadOnlyList<float[]> Checked(IReadOnlyList<float[]> vectors, int expected, EmbeddingOptions options)
    {
        if (vectors.Count != expected || vectors.Any(v => v.Length != options.Dimensions))
        {
            throw new InvalidOperationException(
                $"The '{options.Provider}' model '{options.Model}' returned {vectors.Count} vector(s) for {expected} input(s), "
                + $"or vectors of a size other than Embeddings:Dimensions = {options.Dimensions}.");
        }

        return vectors;
    }
}

public static class EmbeddingServiceCollectionExtensions
{
    /// <summary>Registers the <see cref="IEmbeddingModel"/> named by <c>Embeddings:Provider</c> (default <c>ollama</c>).</summary>
    public static IServiceCollection AddEmbeddingModel(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>() ?? new EmbeddingOptions();

        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .Validate(o => o.Dimensions > 0, "Embeddings:Dimensions must be positive.")
            .Validate(
                o => o.NormalisedProvider is EmbeddingOptions.Ollama or EmbeddingOptions.OpenAiCompatible or EmbeddingOptions.Hashing,
                "Embeddings:Provider must be 'ollama', 'openai-compatible' or 'hashing'.")
            .Validate(
                o => o.NormalisedProvider == EmbeddingOptions.Hashing || Uri.TryCreate(o.EffectiveBaseUrl, UriKind.Absolute, out _),
                "Embeddings:BaseUrl must be an absolute URL for the 'ollama' and 'openai-compatible' providers.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Embeddings:Model is required.")
            .ValidateOnStart();

        static void Configure(IServiceProvider sp, HttpClient client)
        {
            var o = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            client.BaseAddress = new Uri(o.EffectiveBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        }

        switch (settings.NormalisedProvider)
        {
            case EmbeddingOptions.OpenAiCompatible:
                services.AddHttpClient<IEmbeddingModel, OpenAiCompatibleEmbeddingModel>(Configure);
                break;
            case EmbeddingOptions.Hashing:
                services.AddSingleton<IEmbeddingModel>(new HashingEmbeddingModel(settings.Dimensions));
                break;
            default:
                // Ollama is the default (D4); an unknown name fails the validation above at startup.
                services.AddHttpClient<IEmbeddingModel, OllamaEmbeddingModel>(Configure);
                break;
        }

        return services;
    }
}
