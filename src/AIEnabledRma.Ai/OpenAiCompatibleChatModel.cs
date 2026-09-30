using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIEnabledRma.Domain.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Ai;

/// <summary>
/// Reference implementation of <see cref="IChatModel"/> against any OpenAI-compatible
/// <c>/chat/completions</c> endpoint. It exists so that connecting a real model is a
/// configuration change, not a coding task.
///
/// It works unchanged against OpenAI, Azure OpenAI, Ollama, vLLM, LM Studio, llama.cpp's
/// server, Together, Groq, and most gateways, because they all speak the same wire format.
/// Point <see cref="OpenAiCompatibleOptions.BaseUrl"/> at your deployment and set
/// <c>Ai:Provider</c> to <c>openai-compatible</c>.
///
/// Notes on safety, which is why this class is deliberately thin:
///   - The system prompt and the retrieved context are assembled by
///     <c>TriagePipeline</c>, not here. This class does no prompt construction of its own,
///     so there is no second place where a guardrail could be forgotten.
///   - It returns raw text. Schema validation, citation verification, and the fail-closed
///     fallback all happen upstream, so a provider that ignores
///     <see cref="ChatRequest.ResponseSchemaJson"/> is still handled safely.
///   - API keys are read from configuration and never logged.
/// </summary>
public sealed class OpenAiCompatibleChatModel(
    HttpClient http,
    IOptions<OpenAiCompatibleOptions> options,
    ILogger<OpenAiCompatibleChatModel> logger) : IChatModel
{
    private readonly OpenAiCompatibleOptions _options = options.Value;

    public string ProviderName => _options.ProviderName;

    public async Task<ChatCompletion> CompleteAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = _options.BaseUrl.TrimEnd('/') + "/" + _options.CompletionsPath.TrimStart('/');

        var payload = new ChatCompletionRequest
        {
            Model = _options.Model,
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens,
            Messages = request.Messages
                .Select(m => new ChatCompletionMessage { Role = m.Role, Content = m.Content })
                .ToList(),

            // Sent only when the caller supplied a schema. Providers that do not understand
            // it ignore the field; providers that do get structured output for free.
            ResponseFormat = request.ResponseSchemaJson is null
                ? null
                : new ChatCompletionResponseFormat
                {
                    Type = _options.JsonModeValue,
                    JsonSchema = _options.SendJsonSchema
                        ? new ChatCompletionJsonSchema
                        {
                            Name = "triage_verdict",
                            Schema = JsonDocument.Parse(request.ResponseSchemaJson).RootElement,
                            Strict = true,
                        }
                        : null,
                },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, url);

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        message.Content = new StringContent(
            JsonSerializer.Serialize(payload, SerializerOptions),
            Encoding.UTF8,
            "application/json");

        // Propagate the correlation id so a support investigation can be traced from a
        // customer-facing triage decision through to the provider's own logs.
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
        }

        using var response = await http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "AI provider returned {StatusCode}. Body (truncated): {Body}",
                (int)response.StatusCode,
                Truncate(body, 500));

            // Thrown rather than returned, so TriagePipeline's catch produces the
            // fail-closed fallback rather than an unverified verdict.
            response.EnsureSuccessStatusCode();
        }

        var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
            SerializerOptions,
            cancellationToken);

        var choice = completion?.Choices?.FirstOrDefault();
        if (choice?.Message?.Content is not { Length: > 0 } text)
        {
            throw new InvalidOperationException(
                $"AI provider '{ProviderName}' returned an empty completion.");
        }

        return new ChatCompletion
        {
            Text = text,
            FinishReason = choice.FinishReason,
            PromptTokens = completion?.Usage?.PromptTokens ?? 0,
            CompletionTokens = completion?.Usage?.CompletionTokens ?? 0,
        };
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}

public sealed class OpenAiCompatibleOptions
{
    public const string SectionName = "Ai:OpenAiCompatible";

    /// <summary>Name reported to logs and to the diagnostics endpoint.</summary>
    public string ProviderName { get; set; } = "openai-compatible";

    /// <summary>
    /// Base URL of the deployment, e.g. "https://api.example.com/v1" or
    /// "http://localhost:11434/v1". No trailing slash required.
    /// </summary>
    public required string BaseUrl { get; set; }

    public string CompletionsPath { get; set; } = "chat/completions";

    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// API key. Supply via environment variable
    /// <c>Ai__OpenAiCompatible__ApiKey</c> or user secrets; never commit it.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Value for the provider's <c>response_format.type</c>. Most deployments accept
    /// <c>json_object</c>; some want <c>json_schema</c>.
    /// </summary>
    public string JsonModeValue { get; set; } = "json_object";

    /// <summary>
    /// Send the full JSON schema as well as the mode. Turn this on for providers that
    /// support strict structured output.
    /// </summary>
    public bool SendJsonSchema { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Fail the request rather than fall back to unconstrained generation when the provider
    /// is unreachable. Leave this on: the fail-closed path in TriagePipeline is the
    /// designed behaviour, and silently degrading to free-form text would bypass it.
    /// </summary>
    public bool ThrowOnFailure { get; set; } = true;
}

// Wire types. Named to match the OpenAI schema; the snake_case naming policy maps the
// PascalCase property names onto the wire format.
internal sealed class ChatCompletionRequest
{
    public required string Model { get; init; }

    public decimal Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }

    public required IReadOnlyList<ChatCompletionMessage> Messages { get; init; }

    [JsonPropertyName("response_format")]
    public ChatCompletionResponseFormat? ResponseFormat { get; init; }
}

internal sealed class ChatCompletionMessage
{
    public required string Role { get; init; }

    public required string Content { get; init; }
}

internal sealed class ChatCompletionResponseFormat
{
    public required string Type { get; init; }

    [JsonPropertyName("json_schema")]
    public ChatCompletionJsonSchema? JsonSchema { get; init; }
}

internal sealed class ChatCompletionJsonSchema
{
    public required string Name { get; init; }

    [JsonPropertyName("schema")]
    public JsonElement Schema { get; init; }

    public bool Strict { get; init; }
}

internal sealed class ChatCompletionResponse
{
    public IReadOnlyList<ChatCompletionChoice>? Choices { get; init; }

    public ChatCompletionUsage? Usage { get; init; }
}

internal sealed class ChatCompletionChoice
{
    public ChatCompletionMessage? Message { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

internal sealed class ChatCompletionUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }
}
