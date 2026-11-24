using AIEnabledRma.Domain.Operations;

namespace AIEnabledRma.Tests;

/// <summary>
/// A clean review queue is what a healthy nightly sweep and a timer that stopped a week ago both
/// leave behind, so what the dashboard and the gated `/health` details say about the last run is
/// pinned: never having run, having failed, and being late must each read as something other than
/// healthy.
/// </summary>
public sealed class SweepVisibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_sweep_that_never_ran_is_not_reported_as_a_clean_queue()
    {
        var status = SweepStatus.For(SweepSources.PaymentReconciliation, null, Now, SweepStatus.NightlyStaleAfter);

        Assert.True(status.IsStale);
        Assert.True(status.NeedsAttention);
        Assert.Equal(
            "reconcile-payments has not recorded a run: nothing here says the sweep is still running.",
            status.Summary);
        Assert.Equal("never-ran", status.Health().Outcome);
        Assert.Null(status.Health().LastRunAtUtc);
    }

    /// <summary>
    /// The timer fires nightly at 02:40, so a run this old means a whole night went by with nobody
    /// asking the provider anything - the case the review queue cannot show on its own.
    /// </summary>
    [Fact]
    public void A_run_older_than_the_nightly_window_names_the_timer_as_the_problem()
    {
        var status = SweepStatus.For(
            SweepSources.PaymentReconciliation,
            Run(at: Now.AddHours(-30)),
            Now,
            SweepStatus.NightlyStaleAfter);

        Assert.True(status.IsStale);
        Assert.False(status.LastRunFailed);
        Assert.True(status.NeedsAttention);
        Assert.Equal(
            "reconcile-payments last ran 2026-11-23 03:00Z, 30 hour(s) ago and is past its 26-hour "
            + "window: the timer may have stopped.",
            status.Summary);
        Assert.Equal("succeeded", status.Health().Outcome);
    }

    /// <summary>
    /// A recent run that could not ask about every transaction is a failure, not a quiet night: the
    /// command exits 2 for it and the row has to say the same thing.
    /// </summary>
    [Fact]
    public void A_recent_run_that_could_not_ask_about_everything_is_flagged_as_failed()
    {
        var status = SweepStatus.For(
            SweepSources.PaymentReconciliation,
            Run(at: Now.AddHours(-2), succeeded: false, @checked: 12, filed: 1, unanswered: 1),
            Now,
            SweepStatus.NightlyStaleAfter);

        Assert.False(status.IsStale);
        Assert.True(status.LastRunFailed);
        Assert.True(status.NeedsAttention);
        Assert.Equal(
            "reconcile-payments last ran 2026-11-24 07:00Z and did not complete: 12 transaction(s) "
            + "asked about, 1 disagreement(s) filed, 1 unanswered.",
            status.Summary);
        Assert.Equal("failed", status.Health().Outcome);
        Assert.True(status.Health().NeedsAttention);
    }

    [Fact]
    public void A_fresh_successful_run_is_not_flagged_and_says_what_was_asked()
    {
        var status = SweepStatus.For(
            SweepSources.PaymentReconciliation,
            Run(at: Now.AddHours(-2), @checked: 40),
            Now,
            SweepStatus.NightlyStaleAfter);

        Assert.False(status.IsStale);
        Assert.False(status.NeedsAttention);
        Assert.Equal(
            "reconcile-payments last ran 2026-11-24 07:00Z, 2 hour(s) ago, and every transaction it "
            + "asked about answered.",
            status.Summary);
    }

    /// <summary>
    /// The one number that matters to a monitor is whether the field and the sentence agree: the
    /// health detail is the same reading, not a second one computed somewhere else.
    /// </summary>
    [Fact]
    public void The_health_detail_carries_the_same_reading_as_the_dashboard_sentence()
    {
        var status = SweepStatus.For(
            SweepSources.PaymentReconciliation,
            Run(at: Now.AddHours(-30)),
            Now,
            SweepStatus.NightlyStaleAfter);

        var health = status.Health();

        Assert.Equal(SweepSources.PaymentReconciliation, health.Sweep);
        Assert.Equal(Now.AddHours(-30), health.LastRunAtUtc);
        Assert.Equal(30d, health.AgeHours);
        Assert.True(health.Stale);
        Assert.True(health.NeedsAttention);
        Assert.Equal(status.Summary, health.Summary);
    }

    /// <summary>
    /// A ledger nobody can read is not a healthy sweep: the same "unknown" that says the timer's
    /// state is not known, rather than the silence that says everything is fine.
    /// </summary>
    [Fact]
    public void A_ledger_that_cannot_be_read_is_not_reported_as_healthy()
    {
        var unknown = SweepHealthDetails.Unknown(
            SweepSources.PaymentReconciliation,
            "the database did not answer");

        Assert.Equal("unknown", unknown.Outcome);
        Assert.True(unknown.NeedsAttention);
        Assert.Null(unknown.LastRunAtUtc);
        Assert.Contains("not the same as a clean queue", unknown.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row about another job is not evidence about this one; reading it as evidence is how one
    /// working sweep would make every other job look healthy.
    /// </summary>
    [Fact]
    public void A_run_of_another_sweep_is_not_evidence_about_this_one()
    {
        var other = Run(at: Now.AddHours(-1), source: "audit-payments");

        var thrown = Assert.Throws<ArgumentException>(() =>
            SweepStatus.For(SweepSources.PaymentReconciliation, other, Now, SweepStatus.NightlyStaleAfter));

        Assert.Equal("last", thrown.ParamName);
    }

    private static SweepRun Run(
        DateTimeOffset at,
        string source = SweepSources.PaymentReconciliation,
        bool succeeded = true,
        int @checked = 0,
        int filed = 0,
        int unanswered = 0,
        string? detail = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Source = source,
            AtUtc = at,
            Succeeded = succeeded,
            Checked = @checked,
            Filed = filed,
            Unanswered = unanswered,
            Detail = detail,
        };
}
