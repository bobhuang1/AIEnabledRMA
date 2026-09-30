using AIEnabledRma.Domain.Triage;

namespace AIEnabledRma.Domain.Abstractions;

/// <summary>
/// The seam every AI vendor plugs into. The rest of the system depends on this interface and
/// never on a vendor SDK, so swapping providers is a DI registration, not a rewrite.
/// </summary>
public interface IChatModel
{
    /// <summary>Stable identifier for the active provider, e.g. "example-local" or "openai".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Produces a raw completion. Implementations are expected to return text, not to parse it;
    /// schema validation happens in the triage pipeline so that a misbehaving or hostile
    /// provider cannot bypass validation by returning a pre-built object.
    /// </summary>
    Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal chat request. Deliberately not modelled on any vendor's message type.
/// </summary>
public sealed record ChatRequest
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Instructs the provider to return JSON matching the triage schema. Providers that
    /// support native structured output map this to their equivalent; providers that do not
    /// are still safe because the pipeline validates and fails closed.
    /// </summary>
    public string? ResponseSchemaJson { get; init; }

    public decimal Temperature { get; init; } = 0.1m;

    public int MaxOutputTokens { get; init; } = 900;

    /// <summary>Correlation id, passed through to provider logs for support investigations.</summary>
    public string? CorrelationId { get; init; }
}

public sealed record ChatMessage
{
    public required string Role { get; init; }

    public required string Content { get; init; }
}

public sealed record ChatCompletion
{
    public required string Text { get; init; }

    public string? FinishReason { get; init; }

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }
}

/// <summary>
/// Retrieval over the knowledge base. Implemented by the RAG service client so the web and
/// MCP processes can consume the same corpus without re-implementing ranking.
/// </summary>
public interface IKnowledgeRetriever
{
    Task<KnowledgeRetrievalResult> RetrieveAsync(
        KnowledgeRetrievalQuery query,
        CancellationToken cancellationToken);
}

public sealed record KnowledgeRetrievalQuery
{
    public required string Query { get; init; }

    public required IReadOnlySet<string> ScopeTokens { get; init; }

    public int TopK { get; init; } = 6;

    /// <summary>Problem categories to boost, derived from the device's product category.</summary>
    public IReadOnlyList<string> CategoryHints { get; init; } = [];
}

public sealed record KnowledgeRetrievalResult
{
    public IReadOnlyList<RetrievedArticle> Articles { get; init; } = [];

    /// <summary>True when the corpus itself has no articles matching any scope token.</summary>
    public bool CorpusOutOfScope { get; init; }
}

/// <summary>
/// The single entry point for AI-assisted triage. One implementation, enforced by tests:
/// it is the only component permitted to call <see cref="IChatModel"/>.
/// </summary>
public interface ITriageService
{
    Task<TriageResult> TriageAsync(TriageRequest request, CancellationToken cancellationToken);
}

public sealed record TriageRequest
{
    public required IReadOnlyList<TriageDeviceContext> Devices { get; init; }

    public required IReadOnlyList<TriageTurn> Conversation { get; init; }

    public string? CustomerStatement { get; init; }

    public string? RegionCode { get; init; }

    public string? CorrelationId { get; init; }
}
