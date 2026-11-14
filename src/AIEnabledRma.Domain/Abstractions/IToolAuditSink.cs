namespace AIEnabledRma.Domain.Abstractions;

/// <summary>
/// One audited agent call. The arguments are hashed, never stored: an audit trail that keeps the
/// values becomes a second copy of the customer data it exists to protect.
/// </summary>
public sealed record ToolCallRecord
{
    public required string Tool { get; init; }

    /// <summary>Who called: a named local caller over stdio, or an authenticated remote caller.</summary>
    public required string Caller { get; init; }

    /// <summary>Argument name and a truncated hash, one per supplied argument.</summary>
    public required IReadOnlyList<string> ArgumentHashes { get; init; }

    public int ResultCount { get; init; }

    public long LatencyMs { get; init; }

    /// <summary>True when the call was refused, e.g. by a budget or an exactness rule.</summary>
    public bool Refused { get; init; }

    public DateTimeOffset AtUtc { get; init; }
}

/// <summary>Where agent calls are recorded. Implementations must not throw for a call in flight.</summary>
public interface IToolAuditSink
{
    Task RecordAsync(ToolCallRecord record, CancellationToken cancellationToken);
}
