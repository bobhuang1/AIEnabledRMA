using AIEnabledRma.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Data;

/// <summary>
/// Deletes audit rows past their retention window, and answers how many each table would lose.
/// </summary>
/// <remarks>
/// <para>
/// The count a dry run prints and the delete an applied run performs come from the same
/// <see cref="IQueryable{T}"/>, built once per table, so the number an operator approves cannot
/// differ from the rows the next command removes. Building the predicate twice — once for the
/// report, once for the delete — is exactly how a "dry run says 12" turns into "deleted 9".
/// </para>
/// <para>
/// Each predicate is on the column the table already indexes for time (<c>ix_mcp_tool_calls_at</c>,
/// <c>ix_operator_actions_at</c>, <c>ix_webhook_events_received</c>) or, for review items, on the
/// status that leads <c>ix_review_items_status_created</c>. A prune is then a range scan of the rows
/// it means to remove rather than a scan of the whole audit trail.
/// </para>
/// <para>
/// Resolved review items are aged from <c>ResolvedAtUtc</c>, not from creation: an item opened in
/// January and closed yesterday is a day old as far as the queue is concerned, and ageing it by
/// creation would delete a resolution an operator just wrote.
/// </para>
/// </remarks>
public static class RetentionPruner
{
    public static async Task<PruneReport> PruneAsync(
        RmaDbContext db,
        RetentionPolicy policy,
        DateTimeOffset now,
        RetentionTargets targets = RetentionTargets.All,
        bool apply = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(policy);
        policy.EnsureUsable();

        if (targets == RetentionTargets.None)
        {
            throw new ArgumentException("Name at least one table to prune.", nameof(targets));
        }

        var tables = new List<PrunedTable>(5);

        if (targets.HasFlag(RetentionTargets.McpToolCalls))
        {
            var cutoff = policy.McpToolCallsBefore(now);
            var old = db.McpToolCalls.Where(call => call.AtUtc < cutoff);
            tables.Add(new PrunedTable(
                RetentionTargets.McpToolCalls,
                "mcp_tool_calls",
                cutoff,
                await CountOrDeleteAsync(old, apply, cancellationToken)));
        }

        if (targets.HasFlag(RetentionTargets.OperatorActions))
        {
            var cutoff = policy.OperatorActionsBefore(now);
            var old = db.OperatorActions.Where(action => action.AtUtc < cutoff);
            tables.Add(new PrunedTable(
                RetentionTargets.OperatorActions,
                "operator_actions",
                cutoff,
                await CountOrDeleteAsync(old, apply, cancellationToken)));
        }

        if (targets.HasFlag(RetentionTargets.ReviewItems))
        {
            var cutoff = policy.ResolvedReviewItemsBefore(now);
            // Open items are excluded twice over — by the status and by the null resolution time —
            // so a row written before the resolution column existed can never be mistaken for an
            // old resolved one. There is deliberately no age at which an open item becomes eligible.
            var resolvedAndOld = db.ReviewItems.Where(item =>
                item.Status == ReviewStatuses.Resolved
                && item.ResolvedAtUtc != null
                && item.ResolvedAtUtc < cutoff);
            tables.Add(new PrunedTable(
                RetentionTargets.ReviewItems,
                "review_items",
                cutoff,
                await CountOrDeleteAsync(resolvedAndOld, apply, cancellationToken)));
        }

        if (targets.HasFlag(RetentionTargets.WebhookEvents))
        {
            var cutoff = policy.WebhookEventsBefore(now);
            var old = db.WebhookEvents.Where(delivery => delivery.ReceivedAtUtc < cutoff);
            tables.Add(new PrunedTable(
                RetentionTargets.WebhookEvents,
                "webhook_events",
                cutoff,
                await CountOrDeleteAsync(old, apply, cancellationToken)));
        }

        if (targets.HasFlag(RetentionTargets.TriageRefusals))
        {
            var cutoff = policy.TriageRefusalsBefore(now);
            var old = db.TriageRefusals.Where(refusal => refusal.AtUtc < cutoff);
            tables.Add(new PrunedTable(
                RetentionTargets.TriageRefusals,
                "triage_refusals",
                cutoff,
                await CountOrDeleteAsync(old, apply, cancellationToken)));
        }

        return new PruneReport(tables, apply);
    }

    /// <summary>
    /// The count and the delete take the same query, so "what the dry run showed" and "what the
    /// applied run removed" cannot drift apart.
    /// </summary>
    private static Task<int> CountOrDeleteAsync<T>(
        IQueryable<T> rows,
        bool apply,
        CancellationToken cancellationToken)
        where T : class =>
        apply ? rows.ExecuteDeleteAsync(cancellationToken) : rows.CountAsync(cancellationToken);
}
