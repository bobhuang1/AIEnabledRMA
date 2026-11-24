using System.Globalization;

namespace AIEnabledRma.Domain.Operations;

/// <summary>
/// What the health details and the operator dashboard say about one scheduled sweep: when it last
/// ran, how that run ended, and whether either answer means somebody should look.
/// </summary>
/// <remarks>
/// Nothing in the queue distinguishes "asked the provider and everything agreed" from "stopped
/// asking a week ago": a clean queue is what a healthy sweep and a dead timer both leave behind. The
/// run row is what tells them apart, and this is the reading of it both surfaces share, so the
/// sentence an operator reads and the fields a monitor scrapes cannot disagree.
/// </remarks>
public sealed record SweepStatus(string Source, SweepRun? Last, TimeSpan? Age, TimeSpan StaleAfter)
{
    /// <summary>
    /// How late a nightly sweep may be before its silence is reported. The timer fires at 02:40, so a
    /// missed night is a full day with no run; the two hours of slack mean a host that ran it late is
    /// not reported while a night nobody ran it is.
    /// </summary>
    public static readonly TimeSpan NightlyStaleAfter = TimeSpan.FromHours(26);

    /// <summary>
    /// Never having recorded a run counts as stale. A deployment whose timer was never installed is
    /// the case this exists for, and a window that only failed an old row would call it healthy.
    /// </summary>
    public bool IsStale => Age is null || Age > StaleAfter;

    public bool LastRunFailed => Last is { Succeeded: false };

    public bool NeedsAttention => IsStale || LastRunFailed;

    public static SweepStatus For(string source, SweepRun? last, DateTimeOffset now, TimeSpan staleAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);

        if (last is not null && !string.Equals(last.Source, source, StringComparison.Ordinal))
        {
            // A row about some other job is not evidence about this one. Reading it as such would
            // report the nightly reconciliation as healthy on the strength of another sweep's run.
            throw new ArgumentException(
                $"A run of '{last.Source}' says nothing about '{source}'.", nameof(last));
        }

        return new SweepStatus(source, last, last is null ? null : now - last.AtUtc, staleAfter);
    }

    /// <summary>The sentence the dashboard prints, and the same words in the health detail.</summary>
    public string Summary => Last switch
    {
        null => $"{Source} has not recorded a run: nothing here says the sweep is still running.",

        // A failed run is reported as failed even when it is recent: the queue it left behind is the
        // queue nobody can trust, and its age says nothing about that.
        { Succeeded: false } failed => $"{Source} last ran {When(failed.AtUtc)} and did not complete: "
            + $"{failed.Checked} transaction(s) asked about, {failed.Filed} disagreement(s) filed, "
            + $"{failed.Unanswered} unanswered"
            + (string.IsNullOrWhiteSpace(failed.Detail) ? "." : $" ({failed.Detail})."),

        _ when IsStale => $"{Source} last ran {When(Last!.AtUtc)}, {Ago(Age!.Value)} ago and is past its "
            + $"{StaleAfter.TotalHours:0.#}-hour window: the timer may have stopped.",

        _ => $"{Source} last ran {When(Last!.AtUtc)}, {Ago(Age!.Value)} ago, "
            + "and every transaction it asked about answered.",
    };

    /// <summary>The same facts as fields, for the health detail a monitor scrapes.</summary>
    public SweepHealthDetails Health() => new(
        Source,
        Last is null ? "never-ran" : Last.Succeeded ? "succeeded" : "failed",
        Last?.AtUtc,
        Age is { } age ? Math.Round(age.TotalHours, 1) : null,
        IsStale,
        NeedsAttention,
        Summary);

    private static string When(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";

    private static string Ago(TimeSpan age) => age.TotalHours < 1
        ? $"{age.TotalMinutes:0} minute(s)"
        : $"{age.TotalHours:0.#} hour(s)";
}

/// <summary>The sweep facts `/health` publishes inside its gated details.</summary>
public sealed record SweepHealthDetails(
    string Sweep,
    string Outcome,
    DateTimeOffset? LastRunAtUtc,
    double? AgeHours,
    bool Stale,
    bool NeedsAttention,
    string Summary)
{
    /// <summary>
    /// What the endpoint answers when the row cannot be read. Not "ok": an unreadable ledger says the
    /// same thing as no sweep at all, which is the one situation this is here to stop being silent.
    /// </summary>
    public static SweepHealthDetails Unknown(string source, string reason) =>
        new(source, "unknown", null, null, Stale: true, NeedsAttention: true,
            $"{source}: the last run could not be read ({reason}), which is not the same as a clean queue.");
}
