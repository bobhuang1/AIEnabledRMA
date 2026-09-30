using System.Text.Json.Serialization;
using AIEnabledRma.Domain.Catalog;

namespace AIEnabledRma.Domain.Triage;

/// <summary>
/// The only things the AI is ever allowed to say. The model returns this shape verbatim;
/// anything else fails validation and the flow falls back to a human.
/// </summary>
public sealed record TriageVerdict
{
    [JsonPropertyName("resolved")]
    public bool Resolved { get; init; }

    /// <summary>0.0 to 1.0. Below policy threshold the request is routed to a human.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    /// <summary>
    /// Short customer-facing explanation of what the AI concluded. Rendered as plain text and
    /// always Razor/HTML-encoded by the view; never trusted as markup.
    /// </summary>
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    /// <summary>Problem category code the AI matched, validated against the configured list.</summary>
    [JsonPropertyName("problem_category_code")]
    public string? ProblemCategoryCode { get; init; }

    /// <summary>
    /// Step-by-step guidance drawn from retrieved knowledge-base articles, in the order the
    /// customer should try them. Each step must trace to a retrieved article.
    /// </summary>
    [JsonPropertyName("steps")]
    public IReadOnlyList<TriageStep> Steps { get; init; } = [];

    /// <summary>Article ids the AI claims to have used. Every one is checked against the retrieval set.</summary>
    [JsonPropertyName("article_ids")]
    public IReadOnlyList<string> ArticleIds { get; init; } = [];

    /// <summary>
    /// Set when the AI is answering a question outside its permitted scope. The service
    /// returns a canned refusal without calling the model when this is detected upstream.
    /// </summary>
    [JsonPropertyName("out_of_scope")]
    public bool OutOfScope { get; init; }
}

public sealed record TriageStep
{
    [JsonPropertyName("instruction")]
    public string Instruction { get; init; } = string.Empty;

    [JsonPropertyName("article_id")]
    public string? ArticleId { get; init; }
}

/// <summary>
/// A device as presented to the AI. Deliberately narrow: model, product name, and firmware.
/// No customer name, address, email, or phone is ever included, so a prompt-injection
/// attempt cannot exfiltrate PII through the model.
/// </summary>
public sealed record TriageDeviceContext
{
    public required string SerialNumber { get; init; }

    public required string ProductName { get; init; }

    public string? Sku { get; init; }

    public string? Model { get; init; }

    public string? ProductCategory { get; init; }

    public string? FirmwareVersion { get; init; }

    public bool IsInWarranty { get; init; }

    public DateOnly? WarrantyEndDate { get; init; }

    public string? WarrantyPlanName { get; init; }

    /// <summary>Population device-identifier tokens the scope guard matches on.</summary>
    public IReadOnlySet<string> ScopeTokens { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One turn of a troubleshooting conversation, as stored and replayed.
/// </summary>
public sealed record TriageTurn
{
    public required TriageRole Role { get; init; }

    public required string Content { get; init; }

    public DateTimeOffset AtUtc { get; init; }
}

public enum TriageRole
{
    Customer = 0,
    Assistant = 1,
    System = 2,
}

/// <summary>
/// Everything the triage service returns to the web layer: the verdict plus the retrieval
/// provenance the UI shows to the customer and to support staff.
/// </summary>
public sealed record TriageResult
{
    public required TriageVerdict Verdict { get; init; }

    public required IReadOnlyList<RetrievedArticle> Sources { get; init; }

    /// <summary>True when the model was never called because the question was out of scope.</summary>
    public bool RefusedByScopeGuard { get; init; }

    /// <summary>True when the model call failed and the fallback decision was used.</summary>
    public bool UsedFallback { get; init; }

    public string? FailureReason { get; init; }
}

public sealed record RetrievedArticle
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required double Score { get; init; }

    public string? Snippet { get; init; }
}
