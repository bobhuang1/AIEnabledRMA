using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AIEnabledRma.Domain.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Ai;

/// <summary>Embedding model selection (<c>Embeddings</c> section). Plan D4.</summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>
    /// <c>hashing</c> (default, offline, deterministic) or <c>openai-compatible</c> (any
    /// <c>/embeddings</c> endpoint, e.g. a local Ollama at http://localhost:11434/v1).
    /// </summary>
    public string Provider { get; set; } = "hashing";

    public string? BaseUrl { get; set; }

    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>Vector size. Must match the model; the store refuses a mismatch.</summary>
    public int Dimensions { get; set; } = 384;

    /// <summary>Optional bearer key, from the environment or a secret store only.</summary>
    public string? ApiKey { get; set; }

    public int BatchSize { get; set; } = 32;

    public int MaxAttempts { get; set; } = 3;

    public int TimeoutSeconds { get; set; } = 60;
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
            results.AddRange(await EmbedBatchAsync(batch, cancellationToken));
        }

        return results;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(string[] batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
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

                var vectors = body.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();
                if (vectors.Count != batch.Length || vectors.Any(v => v.Length != _options.Dimensions))
                {
                    throw new InvalidOperationException(
                        $"The embedding endpoint returned {vectors.Count} vector(s) for {batch.Length} input(s), "
                        + $"or a size other than the configured {_options.Dimensions}.");
                }

                return vectors;
            }
            catch (Exception ex) when (attempt < _options.MaxAttempts
                && ex is HttpRequestException or TaskCanceledException
                && !cancellationToken.IsCancellationRequested)
            {
                // Counts only: chunk text is never logged.
                logger.LogWarning(ex, "Embedding batch of {Count} failed (attempt {Attempt}); retrying.", batch.Length, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken);
            }
        }
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

public static class EmbeddingServiceCollectionExtensions
{
    /// <summary>Registers the <see cref="IEmbeddingModel"/> named by <c>Embeddings:Provider</c>.</summary>
    public static IServiceCollection AddEmbeddingModel(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>() ?? new EmbeddingOptions();
        var remote = string.Equals(settings.Provider, "openai-compatible", StringComparison.OrdinalIgnoreCase);

        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .Validate(o => o.Dimensions > 0, "Embeddings:Dimensions must be positive.")
            .Validate(
                o => !remote || Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _),
                "Embeddings:BaseUrl must be an absolute URL when Embeddings:Provider is 'openai-compatible'.")
            .Validate(
                o => remote || string.Equals(o.Provider, "hashing", StringComparison.OrdinalIgnoreCase),
                "Embeddings:Provider must be 'hashing' or 'openai-compatible'.")
            .ValidateOnStart();

        if (remote)
        {
            services.AddHttpClient<IEmbeddingModel, OpenAiCompatibleEmbeddingModel>((sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
                client.BaseAddress = new Uri(o.BaseUrl!.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            });
        }
        else
        {
            services.AddSingleton<IEmbeddingModel>(new HashingEmbeddingModel(settings.Dimensions));
        }

        return services;
    }
}
