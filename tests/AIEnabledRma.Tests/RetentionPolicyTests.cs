using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Domain.Rules;

namespace AIEnabledRma.Tests;

/// <summary>
/// The retention windows are a decision, so they are pinned as data rather than left to whichever
/// constant the command happens to pass. A window widened or shortened here is a reviewable diff
/// with a number, not an edit buried in a query.
/// </summary>
public sealed class RetentionPolicyTests
{
    [Fact]
    public void The_shipped_windows_are_the_ones_that_were_chosen()
    {
        var policy = RetentionPolicy.Default;

        Assert.Equal(TimeSpan.FromDays(90), policy.McpToolCalls);
        Assert.Equal(TimeSpan.FromDays(730), policy.OperatorActions);
        Assert.Equal(TimeSpan.FromDays(90), policy.ResolvedReviewItems);
        Assert.Equal(TimeSpan.FromDays(90), policy.WebhookEvents);
    }

    /// <summary>
    /// The operator trail is kept for the return window: a return that can still be disputed must
    /// still have the record of who moved it. If one of these two numbers moves without the other,
    /// the repository is keeping a decision and forgetting its justification.
    /// </summary>
    /// <remarks>
    /// A deployment that raises <c>RmaPolicy:ReturnWindowDays</c> moves the policy without moving
    /// this window, which is why the README says the two are equal at their defaults rather than
    /// that one is derived from the other. Deriving it would mean the prune command binds policy
    /// configuration, and a mistyped override would silently rewrite an audit window.
    /// </remarks>
    [Fact]
    public void The_operator_trail_is_kept_for_the_default_return_window()
    {
        Assert.Equal(new RmaPolicyOptions().ReturnWindowDays, RetentionPolicy.Default.OperatorActions.Days);
    }

    [Fact]
    public void Each_cutoff_is_its_own_window_before_now()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var policy = new RetentionPolicy
        {
            McpToolCalls = TimeSpan.FromDays(10),
            OperatorActions = TimeSpan.FromDays(20),
            ResolvedReviewItems = TimeSpan.FromDays(30),
            WebhookEvents = TimeSpan.FromDays(40),
        };

        Assert.Equal(now.AddDays(-10), policy.McpToolCallsBefore(now));
        Assert.Equal(now.AddDays(-20), policy.OperatorActionsBefore(now));
        Assert.Equal(now.AddDays(-30), policy.ResolvedReviewItemsBefore(now));
        Assert.Equal(now.AddDays(-40), policy.WebhookEventsBefore(now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_window_that_is_not_positive_is_refused(int days)
    {
        var policy = new RetentionPolicy
        {
            McpToolCalls = TimeSpan.FromDays(90),
            OperatorActions = TimeSpan.FromDays(days),
            ResolvedReviewItems = TimeSpan.FromDays(90),
            WebhookEvents = TimeSpan.FromDays(90),
        };

        var refused = Assert.Throws<InvalidOperationException>(policy.EnsureUsable);

        // A zero window does not mean "keep nothing for now"; it means "delete the row just
        // written", which is the opposite of what the audit tables are for.
        Assert.Contains("positive", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void All_is_every_table_and_no_more()
    {
        var everyTable = RetentionTargets.McpToolCalls
            | RetentionTargets.OperatorActions
            | RetentionTargets.ReviewItems
            | RetentionTargets.WebhookEvents;

        Assert.Equal(RetentionTargets.All, everyTable);
        Assert.Equal(
            4,
            Enum.GetValues<RetentionTargets>()
                .Count(target => target is not RetentionTargets.None and not RetentionTargets.All));
    }
}
