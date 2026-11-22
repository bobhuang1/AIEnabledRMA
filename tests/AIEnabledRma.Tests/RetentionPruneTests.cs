using AIEnabledRma.Data;
using AIEnabledRma.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Tests;

/// <summary>
/// The pruner against PostgreSQL, because the property being claimed is what the database deletes:
/// a predicate that compares the wrong column, drops the status filter, or is applied in a dry run
/// cannot be shown by a fake, and each of those mistakes destroys an audit row nobody can get back.
/// </summary>
public sealed class RetentionPruneTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _database = new("rma_retention");

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();

        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task A_dry_run_counts_what_is_past_its_window_and_deletes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        await using (var db = _database.CreateContext())
        {
            var report = await RetentionPruner.PruneAsync(
                db, RetentionPolicy.Default, Now, cancellationToken: cancellationToken);

            Assert.False(report.Applied);
            Assert.Equal(RetentionTargets.All, report.Tables.Select(table => table.Target).Aggregate((left, right) => left | right));

            // One old row per table; the review queue's other two items are not eligible.
            Assert.All(report.Tables, table => Assert.Equal(1, table.Rows));
            Assert.Equal(4, report.TotalRows);
        }

        // The point of the default: the report is a proposal, and nothing it counted is gone.
        await using var check = _database.CreateContext();
        Assert.Equal(2, await check.McpToolCalls.CountAsync(cancellationToken));
        Assert.Equal(2, await check.OperatorActions.CountAsync(cancellationToken));
        Assert.Equal(2, await check.WebhookEvents.CountAsync(cancellationToken));
        Assert.Equal(3, await check.ReviewItems.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Applying_deletes_only_the_rows_past_their_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        await using (var db = _database.CreateContext())
        {
            var report = await RetentionPruner.PruneAsync(
                db, RetentionPolicy.Default, Now, apply: true, cancellationToken: cancellationToken);

            Assert.True(report.Applied);
            Assert.Equal(4, report.TotalRows);
        }

        await using (var check = _database.CreateContext())
        {
            Assert.Equal(
                new[] { "fresh_call" },
                await check.McpToolCalls.AsNoTracking().Select(call => call.Tool).ToListAsync(cancellationToken));

            Assert.Equal(
                new[] { "fresh_action" },
                await check.OperatorActions.AsNoTracking().Select(action => action.Action).ToListAsync(cancellationToken));

            Assert.Equal(
                new[] { "evt_fresh" },
                await check.WebhookEvents.AsNoTracking().Select(delivery => delivery.EventId).ToListAsync(cancellationToken));

            // "resolved-fresh" was created 400 days ago and resolved 89 days ago: it survives, which
            // is what ageing the queue by resolution rather than by creation buys. "open-old" is
            // older than every window and also survives, because an open item is work, not history.
            Assert.Equal(
                new[] { "open-old", "resolved-fresh" },
                await check.ReviewItems.AsNoTracking().OrderBy(item => item.Reason).Select(item => item.Reason).ToListAsync(cancellationToken));
        }

        await using (var second = _database.CreateContext())
        {
            var again = await RetentionPruner.PruneAsync(
                second, RetentionPolicy.Default, Now, apply: true, cancellationToken: cancellationToken);

            Assert.Equal(0, again.TotalRows);
        }
    }

    [Fact]
    public async Task An_open_review_item_is_never_pruned_however_old_it_is()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var seed = _database.CreateContext())
        {
            seed.ReviewItems.Add(Review("still-open", createdDaysAgo: 4000, resolvedDaysAgo: null));
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using (var db = _database.CreateContext())
        {
            var report = await RetentionPruner.PruneAsync(
                db,
                RetentionPolicy.Default,
                Now,
                RetentionTargets.ReviewItems,
                apply: true,
                cancellationToken);

            Assert.Equal(0, report.TotalRows);
        }

        await using var check = _database.CreateContext();
        Assert.Equal("Open", Assert.Single(await check.ReviewItems.AsNoTracking().ToListAsync(cancellationToken)).Status);
    }

    [Fact]
    public async Task Naming_one_table_leaves_the_others_untouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        await using (var db = _database.CreateContext())
        {
            var report = await RetentionPruner.PruneAsync(
                db,
                RetentionPolicy.Default,
                Now,
                RetentionTargets.WebhookEvents,
                apply: true,
                cancellationToken);

            Assert.Equal(
                new[] { RetentionTargets.WebhookEvents },
                report.Tables.Select(table => table.Target).ToArray());
            Assert.Equal(1, report.TotalRows);
        }

        await using var check = _database.CreateContext();
        Assert.Equal(new[] { "evt_fresh" }, await check.WebhookEvents.AsNoTracking().Select(delivery => delivery.EventId).ToListAsync(cancellationToken));
        Assert.Equal(2, await check.McpToolCalls.CountAsync(cancellationToken));
        Assert.Equal(2, await check.OperatorActions.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task A_row_exactly_at_the_window_is_not_yet_old()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var seed = _database.CreateContext())
        {
            // The window is "older than 90 days", so the boundary itself is still inside it. An
            // off-by-one here deletes a row a day early, every day.
            seed.McpToolCalls.AddRange(
                Call("on_the_boundary", Now.AddDays(-90)),
                Call("one_second_past", Now.AddDays(-90).AddSeconds(-1)));
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using (var db = _database.CreateContext())
        {
            await RetentionPruner.PruneAsync(
                db, RetentionPolicy.Default, Now, RetentionTargets.McpToolCalls, apply: true, cancellationToken);
        }

        await using var check = _database.CreateContext();
        Assert.Equal(
            new[] { "on_the_boundary" },
            await check.McpToolCalls.AsNoTracking().Select(call => call.Tool).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Refuses_an_empty_selection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = _database.CreateContext();

        await Assert.ThrowsAsync<ArgumentException>(() => RetentionPruner.PruneAsync(
            db, RetentionPolicy.Default, Now, RetentionTargets.None, apply: true, cancellationToken));
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var db = _database.CreateContext();

        db.McpToolCalls.AddRange(
            Call("old_call", Now.AddDays(-91)),
            Call("fresh_call", Now.AddDays(-89)));

        db.OperatorActions.AddRange(
            Action("old_action", Now.AddDays(-731)),
            Action("fresh_action", Now.AddDays(-729)));

        db.WebhookEvents.AddRange(
            new ProcessedWebhookEvent { EventId = "evt_old", Type = "payment_intent.succeeded", ReceivedAtUtc = Now.AddDays(-91) },
            new ProcessedWebhookEvent { EventId = "evt_fresh", Type = "payment_intent.succeeded", ReceivedAtUtc = Now.AddDays(-89) });

        db.ReviewItems.AddRange(
            Review("resolved-old", createdDaysAgo: 200, resolvedDaysAgo: 91),
            // Created long before the window, resolved inside it: age is measured from resolution.
            Review("resolved-fresh", createdDaysAgo: 400, resolvedDaysAgo: 89),
            Review("open-old", createdDaysAgo: 400, resolvedDaysAgo: null));

        await db.SaveChangesAsync(cancellationToken);
    }

    private static McpToolCall Call(string tool, DateTimeOffset atUtc) => new()
    {
        Id = Guid.NewGuid(),
        Tool = tool,
        Caller = "stdio:local",
        ArgumentHashes = "serial:0123456789abcdef",
        AtUtc = atUtc,
    };

    private static OperatorAction Action(string action, DateTimeOffset atUtc) => new()
    {
        Id = Guid.NewGuid(),
        AtUtc = atUtc,
        Operator = "operator-demo",
        Action = action,
        Target = "RMA-2026-00001",
        Succeeded = true,
    };

    private static ReviewItem Review(string reason, int createdDaysAgo, int? resolvedDaysAgo) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAtUtc = Now.AddDays(-createdDaysAgo),
        Source = ReviewSources.PolicyHumanReview,
        Reason = reason,
        Status = resolvedDaysAgo is null ? ReviewStatuses.Open : ReviewStatuses.Resolved,
        ResolvedAtUtc = resolvedDaysAgo is null ? null : Now.AddDays(-resolvedDaysAgo.Value),
        ResolvedBy = resolvedDaysAgo is null ? null : "operator-demo",
        Resolution = resolvedDaysAgo is null ? null : "looked at it",
    };
}
