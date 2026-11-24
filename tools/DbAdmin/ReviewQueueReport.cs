using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Operations;

namespace AIEnabledRma.Tools.DbAdmin;

/// <summary>
/// One open review item, plus the two facts only the return it names can answer: which RMA it is
/// about and which transaction.
/// </summary>
/// <remarks>
/// Read from the return rather than copied onto the item, for the reason the dashboard reads them
/// there too: a copy on the item is a second version of the fact, free to drift from the row an
/// operator actually acts on.
/// </remarks>
public sealed record ReviewQueueRow(ReviewItem Item, string? RmaNumber, string? TransactionId);

/// <summary>What a `review-queue` run read: the open items, and whether the bound cut the queue short.</summary>
public sealed record OpenReviewQueueReport(IReadOnlyList<ReviewQueueRow> Rows, bool More);

/// <summary>
/// What `DbAdmin review-queue` prints, kept out of the top-level program so a test can pin it.
/// </summary>
/// <remarks>
/// The dashboard shows this queue to a signed-in operator, but a headless deployment has no browser
/// and no sign-in: without this command the payment mismatch the nightly reconciliation just filed
/// is a row only a psql session can see. Every line carries the five facts an item is judged on, and
/// says "unknown" where the item recorded none, so a missing sum cannot be read as a zero.
/// </remarks>
public static class ReviewQueueReport
{
    /// <summary>How many items one run lists when nobody names a bound, matching the dashboard.</summary>
    public const int DefaultTake = 100;

    private const string Unknown = "unknown";

    /// <summary>
    /// Reads the open items and the returns they name. It reads only: resolving one stays the
    /// dashboard's act, so nothing here can close an item an operator has not seen.
    /// </summary>
    public static async Task<OpenReviewQueueReport> ReadAsync(
        IOperationsRepository operations,
        IRmaRepository requests,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);

        // One row more than asked for, the same shape `audit-payments` uses: the extra row is how
        // the report knows the bound cut the queue short, without a second count that could
        // disagree with the list the operator is reading.
        var items = await operations.OpenReviewItemsAsync(take + 1, cancellationToken);
        var rows = new List<ReviewQueueRow>(Math.Min(items.Count, take));

        foreach (var item in items.Take(take))
        {
            var named = item.RmaRequestId is { } id
                ? await requests.GetByIdAsync(id, cancellationToken)
                : null;

            rows.Add(new ReviewQueueRow(item, named?.RmaNumber, named?.PaymentTransactionId));
        }

        return new OpenReviewQueueReport(rows, items.Count > take);
    }

    public static IReadOnlyList<string> Format(OpenReviewQueueReport report)
    {
        var lines = new List<string>
        {
            $"  open:        {report.Rows.Count}",
            $"  mismatch:    {Mismatches(report)}",
        };

        foreach (var row in report.Rows)
        {
            // The sums are on the line, not only in the sentence: two equal states holding two
            // different amounts is money that moved unrecorded, and it is the one fact the reason
            // code cannot carry.
            lines.Add(
                $"  {row.RmaNumber ?? "(no return)"}: {row.Item.Source} {row.Item.Reason}; "
                + $"transaction {row.TransactionId ?? Unknown}; "
                + $"expected {row.Item.ExpectedMoney?.ToString() ?? Unknown}; "
                + $"provider holds {row.Item.ProviderMoney?.ToString() ?? Unknown}");
        }

        if (report.More)
        {
            // A bounded run that stopped early must not read like the whole queue.
            lines.Add("  more:        --take cut the queue short; re-run with a larger --take to see the rest.");
        }

        return lines;
    }

    /// <summary>The sentence under the list, which is what an operator actually reads.</summary>
    public static string Summary(OpenReviewQueueReport report)
    {
        if (report.Rows.Count == 0)
        {
            return "No open review items.";
        }

                var parts = new List<string> { $"{report.Rows.Count} open item(s) waiting" };
        var mismatches = Mismatches(report);

        if (mismatches > 0)
        {
            parts.Add($"{mismatches} of them a payment mismatch");
        }

        if (report.More)
        {
            parts.Add("the --take bound cut the queue short");
        }

        // Said every time: a listing command that read like a resolved queue would be the next
        // person's wrong assumption.
        return string.Join("; ", parts) + "; this command only lists them.";
    }

    private static int Mismatches(OpenReviewQueueReport report) =>
        report.Rows.Count(row => row.Item.Source == ReviewSources.PaymentMismatch);
}
