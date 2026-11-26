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

    /// <summary>
    /// The caller's stated reason for a call that unmasked something, as written. Null for every
    /// other call.
    /// </summary>
    /// <remarks>
    /// The one value the row keeps: the arguments are hashed because they are the data being read,
    /// while a reason is the justification for reading it, and a support role that unmasked a
    /// customer without leaving the reason behind has left nothing an audit can judge.
    /// </remarks>
    public string? Reason { get; set; }

    public int ResultCount { get; set; }

    public long LatencyMs { get; set; }

    public bool Refused { get; set; }

    public DateTimeOffset AtUtc { get; set; }
}
