namespace AIEnabledRma.Domain.Cases;

/// <summary>
/// A CRM support case as the ingestion pipeline sees it: troubleshooting text only.
/// </summary>
/// <remarks>
/// There is deliberately no customer, account, contact, owner or order field. A source maps a
/// CRM record into this shape and drops those fields there, so they can never reach redaction,
/// logs, the store or a model. Free text can still contain personal data (a signature, a phone
/// number in a note) and is redacted by the pipeline.
/// </remarks>
public sealed record SupportCase
{
    /// <summary>The CRM's record id. Used for idempotency only; never indexed or shown.</summary>
    public required string SourceRecordId { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public string? Resolution { get; init; }

    /// <summary>Product family or model, used for the retrieval scope filter.</summary>
    public string? Product { get; init; }

    /// <summary>Normalised status: <c>Resolved</c>, <c>Active</c>, <c>Cancelled</c>.</summary>
    public required string Status { get; init; }

    public DateTimeOffset? ClosedOn { get; init; }

    public required DateTimeOffset ModifiedOn { get; init; }

    public bool IsResolvedWithResolution =>
        string.Equals(Status, "Resolved", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Resolution);
}

/// <summary>A read-only source of support cases, read incrementally by modified date.</summary>
public interface ICaseSource
{
    string Name { get; }

    IAsyncEnumerable<SupportCase> ReadModifiedSinceAsync(DateTimeOffset? watermark, CancellationToken cancellationToken);
}
