namespace AIEnabledRma.Domain.Operations;

/// <summary>
/// One row per counted tool call, written to the database rather than to process memory.
/// </summary>
/// <remarks>
/// The budget bounds what an agent may do to the data, so it cannot live in the process that serves
/// it: two replicas of the HTTP transport each holding their own counter hand out the full allowance
/// twice, and each one honestly reports itself as within the limit. The rows are the counter, so a
/// caller's window is the same window whichever replica answers. They are deleted as they age out,
/// which is what a rolling window means — the table holds one window's worth of rows per caller,
/// not a history.
/// </remarks>
public sealed class McpCallStamp
{
    public required Guid Id { get; set; }

    /// <summary>The caller as <c>ICallerIdentity.Describe()</c> reports it.</summary>
    public required string Caller { get; set; }

    /// <summary><c>call</c> for any tool call, <c>lookup</c> for a customer lookup.</summary>
    public required string Kind { get; set; }

    public DateTimeOffset AtUtc { get; set; }
}
