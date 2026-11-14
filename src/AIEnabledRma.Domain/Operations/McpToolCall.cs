namespace AIEnabledRma.Domain.Operations;

/// <summary>
/// One row per audited agent tool call. Deliberately holds no argument values and no result
/// values: the point of the row is to answer "who asked what, and how much came back" without
/// becoming a copy of the customer record the masking exists to keep out of transcripts.
/// </summary>
public sealed class McpToolCall
{
    public required Guid Id { get; set; }

    public required string Tool { get; set; }

    public required string Caller { get; set; }

    /// <summary>Comma-separated <c>argument:hash</c> pairs, hashes truncated for readability.</summary>
    public required string ArgumentHashes { get; set; }

    public int ResultCount { get; set; }

    public long LatencyMs { get; set; }

    public bool Refused { get; set; }

    public DateTimeOffset AtUtc { get; set; }
}
