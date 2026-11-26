namespace AIEnabledRma.Domain.Operations;

/// <summary>
/// How long each audit table is kept, and what "old" means for it.
/// </summary>
/// <remarks>
/// These are decisions rather than deployment knobs: the owner chose each window on 2026-11-22 and
/// the reason is written beside it in <see cref="Default"/>. They live in the domain because the
/// window is a rule about the data, not a property of the CLI that happens to apply it — a test can
/// then pin the numbers without a database, and a future job that prunes from somewhere else reads
/// the same policy instead of copying the arithmetic.
/// </remarks>
public sealed record RetentionPolicy
{
    /// <summary>How long a row of <c>mcp_tool_calls</c> is kept after the call.</summary>
    public required TimeSpan McpToolCalls { get; init; }

    /// <summary>How long a row of <c>operator_actions</c> is kept after the action.</summary>
    public required TimeSpan OperatorActions { get; init; }

    /// <summary>
    /// How long a <em>resolved</em> row of <c>review_items</c> is kept after it was resolved. An open
    /// item has no window at all: it is the queue, and deleting it would drop work nobody has done.
    /// </summary>
    public required TimeSpan ResolvedReviewItems { get; init; }

    /// <summary>How long a row of <c>webhook_events</c> is kept after the delivery was recorded.</summary>
    public required TimeSpan WebhookEvents { get; init; }

    /// <summary>
    /// How long a refused question in <c>triage_refusals</c> is kept after it was asked.
    /// </summary>
    public required TimeSpan TriageRefusals { get; init; }

    /// <summary>
    /// The policy this repository ships. Each window has its own reason:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Tool calls, 90 days.</b> The rows hold tool names, callers, argument <em>hashes</em> and
    /// latency — no values — so they are an operational record ("what did agents ask last quarter"),
    /// not personal data that has to be minimised. A quarter covers investigating an agent's
    /// behaviour and then stops paying for it.
    /// </description></item>
    /// <item><description>
    /// <b>Operator actions, 730 days.</b> This is the human audit trail: who received, captured,
    /// voided or resolved what, refusals included. It is kept for exactly the return window
    /// (<c>RmaPolicy:ReturnWindowDays</c>), because an action explains a decision for as long as that
    /// decision can still be disputed. A shorter window would keep the return and forget who moved it.
    /// </description></item>
    /// <item><description>
    /// <b>Resolved review items, 90 days after resolution.</b> The queue is a work list; a closed
    /// item is history. Ninety days keeps a quarter of "why was this sent to a person, and what did
    /// they decide" without the table growing forever. Open items are never pruned at any age.
    /// </description></item>
    /// <item><description>
    /// <b>Webhook deliveries, 90 days.</b> Stripe retries a delivery for about three days, and the
    /// whole point of the row is to recognise that retry, so the window is far longer than the
    /// retry behaviour it guards. Ninety days also outlives any plausible replay of an old event.
    /// </description></item>
    /// <item><description>
    /// <b>Refused questions, 365 days.</b> These rows exist to show a corpus gap rather than to
    /// investigate a person: the statement is already redacted, and the question being answered is
    /// "what do customers ask that no article covers". A year covers a seasonal fault cycle and
    /// the corpus reviews of two quarters, and then stops paying for a sentence nobody acted on.
    /// </description></item>
    /// </list>
    /// </summary>
    public static RetentionPolicy Default { get; } = new()
    {
        McpToolCalls = TimeSpan.FromDays(90),
        OperatorActions = TimeSpan.FromDays(730),
        ResolvedReviewItems = TimeSpan.FromDays(90),
        WebhookEvents = TimeSpan.FromDays(90),
        TriageRefusals = TimeSpan.FromDays(365),
    };

    /// <summary>
    /// Refuses a window that would delete the row just written. Zero or a negative span is not a
    /// shorter retention period, it is a job that eats the audit trail it was asked to bound, so it
    /// fails before anything is deleted rather than reporting a large number as a success.
    /// </summary>
    public void EnsureUsable()
    {
        if (McpToolCalls <= TimeSpan.Zero
            || OperatorActions <= TimeSpan.Zero
            || ResolvedReviewItems <= TimeSpan.Zero
            || WebhookEvents <= TimeSpan.Zero
            || TriageRefusals <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Every retention window must be positive: a zero or negative window prunes the row that was just written.");
        }
    }

    public DateTimeOffset McpToolCallsBefore(DateTimeOffset now) => now - McpToolCalls;

    public DateTimeOffset OperatorActionsBefore(DateTimeOffset now) => now - OperatorActions;

    public DateTimeOffset ResolvedReviewItemsBefore(DateTimeOffset now) => now - ResolvedReviewItems;

    public DateTimeOffset WebhookEventsBefore(DateTimeOffset now) => now - WebhookEvents;

    public DateTimeOffset TriageRefusalsBefore(DateTimeOffset now) => now - TriageRefusals;
}

/// <summary>Which audit tables a prune run touches. None named means all of them.</summary>
[Flags]
public enum RetentionTargets
{
    None = 0,

    McpToolCalls = 1 << 0,

    OperatorActions = 1 << 1,

    ReviewItems = 1 << 2,

    WebhookEvents = 1 << 3,

    TriageRefusals = 1 << 4,

    All = McpToolCalls | OperatorActions | ReviewItems | WebhookEvents | TriageRefusals,
}

/// <summary>
/// What one table's share of a prune run did: the cutoff it used and how many rows that matched
/// (a dry run) or deleted (an applied run).
/// </summary>
public sealed record PrunedTable(RetentionTargets Target, string Table, DateTimeOffset Cutoff, int Rows);

/// <summary>
/// The outcome of a prune run. <paramref name="Applied"/> is false for the default dry run, which is
/// the difference the operator is meant to see: the counts mean "would delete" until it is true.
/// </summary>
public sealed record PruneReport(IReadOnlyList<PrunedTable> Tables, bool Applied)
{
    public int TotalRows => Tables.Sum(table => table.Rows);
}
