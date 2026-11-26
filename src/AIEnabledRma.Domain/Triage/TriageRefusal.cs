namespace AIEnabledRma.Domain.Triage;

/// <summary>
/// One question the scope guard refused, kept so the corpus's blind spots are visible.
/// </summary>
/// <remarks>
/// A refusal is the cheapest evidence this product can produce about what customers ask that the
/// knowledge base cannot answer: the customer was turned away, and until this row existed nothing
/// anywhere recorded what they had asked. The statement is stored after the pipeline's redaction
/// pass, so it holds placeholders rather than the identifiers it was typed with, and it is truncated
/// to the column width rather than being allowed to grow one row into a document.
/// </remarks>
public sealed class TriageRefusal
{
    public Guid Id { get; set; }

    /// <summary>When the guard refused, so the report can be bounded by a window.</summary>
    public DateTimeOffset AtUtc { get; set; }

    /// <summary>
    /// The guard's own reason, in its own words (<c>off-topic</c>, <c>not-this-item</c> and so on).
    /// Kept as written rather than mapped to an enum: the guard is where the vocabulary lives, and a
    /// copy here would be a second list to keep in step.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The redacted statement, which is what makes the row useful for a corpus review.</summary>
    public string Statement { get; set; } = string.Empty;
}
