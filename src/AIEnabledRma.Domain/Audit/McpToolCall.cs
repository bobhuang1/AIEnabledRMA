namespace AIEnabledRma.Domain.Audit;

/// <summary>
/// One row per MCP tool call. Argument values are never stored: each argument is reduced to a
/// truncated SHA-256 of its JSON so repeated probing with the same value is visible, while the
/// value itself (an email, a phone number) stays out of the audit trail.
/// </summary>
public sealed class McpToolCall
{
    public required Guid Id { get; set; }

    public required DateTimeOffset AtUtc { get; set; }

    public required string Tool { get; set; }

    /// <summary>Authenticated client or subject, or the configured local caller for stdio.</summary>
    public required string Caller { get; set; }

    public string? SessionId { get; set; }

    /// <summary>JSON object of argument name to hash prefix.</summary>
    public required string ArgumentHashes { get; set; }

    /// <summary>Items returned, when the result reports a count.</summary>
    public int? ResultCount { get; set; }

    /// <summary><see cref="McpToolCallOutcome"/> as its name.</summary>
    public required string Outcome { get; set; }

    public required int LatencyMs { get; set; }
}

public enum McpToolCallOutcome
{
    Ok,
    ToolError,
    RefusedBudget,
    RefusedPermission,
    Failed,
}
