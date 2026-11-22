using AIEnabledRma.Data;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Tools.DbAdmin;

/// <summary>One table's row count, in the order `stats` prints them.</summary>
public sealed record TableRowCount(string Table, int Rows);

/// <summary>
/// The body of `DbAdmin stats`, kept out of the top-level program so a test can pin what it prints.
/// </summary>
/// <remarks>
/// The four audit tables are here because the question an operator asks of them is different from the
/// one the business tables answer: the business counts describe the site, while these describe how
/// much has accumulated since the last `prune`. A table missing from this list reads exactly like a
/// table that never grows, which is why the list is pinned by a test rather than left to the command.
/// </remarks>
public static class StatsReport
{
    /// <summary>Width of the business-table column, one space past the longest name (`rma_requests:`).</summary>
    private const int TableWidth = 15;

    /// <summary>The audit block is its own column width, because `operator_actions:` is longer.</summary>
    private const int AuditWidth = 18;

    private static readonly string[] AuditTables =
    [
        "mcp_tool_calls",
        "operator_actions",
        "review_items",
        "webhook_events",
    ];

    /// <summary>Every count `stats` reports, read through the application's own model.</summary>
    public static async Task<IReadOnlyList<TableRowCount>> ReadAsync(
        RmaDbContext db,
        CancellationToken cancellationToken = default) =>
    [
        new("products", await db.Products.CountAsync(cancellationToken)),
        new("devices", await db.Devices.CountAsync(cancellationToken)),
        new("warranties", await db.Warranties.CountAsync(cancellationToken)),
        new("customers", await db.Customers.CountAsync(cancellationToken)),
        new("addresses", await db.Addresses.CountAsync(cancellationToken)),
        new("rma_requests", await db.RmaRequests.CountAsync(cancellationToken)),
        new("rma_lines", await db.RmaLines.CountAsync(cancellationToken)),
        new("mcp_tool_calls", await db.McpToolCalls.CountAsync(cancellationToken)),
        new("operator_actions", await db.OperatorActions.CountAsync(cancellationToken)),
        new("review_items", await db.ReviewItems.CountAsync(cancellationToken)),
        new("webhook_events", await db.WebhookEvents.CountAsync(cancellationToken)),
    ];

    /// <summary>
    /// The lines `stats` writes, business tables first and the audit tables in a block of their own.
    /// </summary>
    public static IReadOnlyList<string> Format(IReadOnlyList<TableRowCount> counts)
    {
        var lines = counts
            .Where(count => !IsAudit(count.Table))
            .Select(count => Line(count, TableWidth))
            .ToList();

        // A blank line rather than a heading: the drill and the prune report both parse this output,
        // and a heading that is not a count is one more thing a later reader could mistake for one.
        lines.Add(string.Empty);
        lines.AddRange(counts
            .Where(count => IsAudit(count.Table))
            .Select(count => Line(count, AuditWidth)));

        return lines;
    }

    public static bool IsAudit(string table) => AuditTables.Contains(table, StringComparer.Ordinal);

    private static string Line(TableRowCount count, int width) =>
        "  " + (count.Table + ":").PadRight(width) + count.Rows;
}
