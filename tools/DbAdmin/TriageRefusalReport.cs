using AIEnabledRma.Data;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Tools.DbAdmin;

/// <summary>One distinct refused question: how often it was asked, and when it was last asked.</summary>
public sealed record TriageRefusalRow(string Reason, string Statement, int Count, DateTimeOffset LastSeenUtc);

/// <summary>
/// What a `refusals` run read: the ranked rows, how many refusals the window holds in total, and
/// whether the bound cut the list short.
/// </summary>
public sealed record TriageRefusalDigest(
    IReadOnlyList<TriageRefusalRow> Rows,
    int Refusals,
    bool More,
    DateTimeOffset Since);

/// <summary>
/// What `DbAdmin refusals` prints, kept out of the top-level program so a test can pin it.
/// </summary>
/// <remarks>
/// A refused question is the cheapest evidence of what the corpus cannot answer: the customer was
/// turned away and, until the pipeline started recording them, nothing said what they had asked.
/// Ranking by count is the whole point — one odd phrasing is noise, twelve people describing the
/// same undocumented fault is the next article — so the rows are grouped by the reason and the
/// redacted statement, the two facts a reviewer would quote.
///
/// The statement is whatever the pipeline redacted before the model call, so an operator reading
/// this report sees placeholders where identifiers were typed. That is deliberate: the report exists
/// to show what was asked, not who asked it.
/// </remarks>
public static class TriageRefusalReport
{
    /// <summary>Rows one run lists when nobody names a bound.</summary>
    public const int DefaultTake = 20;

    /// <summary>The window one run reads when nobody names one.</summary>
    public const int DefaultDays = 30;

    /// <summary>How much of the statement a line prints before it is cut, so one long paste cannot wrap a report.</summary>
    private const int StatementWidth = 90;

    private const int ReasonWidth = 24;

    public static async Task<TriageRefusalDigest> ReadAsync(
        RmaDbContext db,
        DateTimeOffset since,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);

        // One row more than asked for, the shape `audit-payments` and `review-queue` already use:
        // the extra row is how the report knows the bound cut the list short without a second
        // grouped query that could disagree with the one being printed.
        var grouped = await db.TriageRefusals
            .Where(refusal => refusal.AtUtc >= since)
            .GroupBy(refusal => new { refusal.Reason, refusal.Statement })
            .Select(group => new
            {
                group.Key.Reason,
                group.Key.Statement,
                Count = group.Count(),
                LastSeen = group.Max(refusal => refusal.AtUtc),
            })
            .OrderByDescending(row => row.Count)
            .ThenByDescending(row => row.LastSeen)
            .ThenBy(row => row.Statement)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        var more = grouped.Count > take;

        var rows = grouped
            .Take(take)
            .Select(row => new TriageRefusalRow(row.Reason, row.Statement, row.Count, row.LastSeen))
            .ToList();

        // The number the ranking is a share of. Counted over rows rather than summed from `rows`,
        // which stop at the bound and would report a truncation as the whole window.
        var refusals = await db.TriageRefusals
            .CountAsync(refusal => refusal.AtUtc >= since, cancellationToken);

        return new TriageRefusalDigest(rows, refusals, more, since);
    }

    public static IReadOnlyList<string> Format(TriageRefusalDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);

        var lines = new List<string>
        {
            $"Refused questions since {digest.Since:yyyy-MM-dd} (most asked first):",
            string.Empty,
        };

        if (digest.Rows.Count == 0)
        {
            lines.Add("  (none)");
            return lines;
        }

        foreach (var row in digest.Rows)
        {
            lines.Add(
                $"  {row.Count,4}x  {Fit(row.Reason, ReasonWidth),-ReasonWidth}  {row.LastSeenUtc:yyyy-MM-dd}  {Fit(row.Statement, StatementWidth)}");
        }

        return lines;
    }

    public static string Summary(TriageRefusalDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);

        if (digest.Refusals == 0)
        {
            return "Nothing was refused in this window.";
        }

        var summary =
            $"{digest.Refusals} refusal(s) across {digest.Rows.Count} distinct statement(s) since {digest.Since:yyyy-MM-dd}.";

        return digest.More
            ? summary + $" Showing the {digest.Rows.Count} most asked; raise --take to see more."
            : summary;
    }

    /// <summary>
    /// Cuts a value to the column width, ending in an ellipsis so a shortened sentence cannot read
    /// as a complete one.
    /// </summary>
    private static string Fit(string value, int width)
    {
        var single = value.ReplaceLineEndings(" ").Trim();

        return single.Length <= width ? single : string.Concat(single.AsSpan(0, width - 1), "…");
    }
}
