using System.Text.Json;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Tools.DbAdmin;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Tests;

/// <summary>
/// `DbAdmin stats` is the only view an operator has of how fast the five audit tables fill between
/// prune runs, so its output is pinned here rather than left to whatever the command happens to
/// print: a table that drops out of the list reads exactly like a table that never grows.
/// </summary>
public sealed class DbAdminStatsTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _database = new("rma_stats");

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();

        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public void The_report_names_every_table_and_keeps_the_two_groups_apart()
    {
        var lines = StatsReport.Format(
        [
            new TableRowCount("products", 5),
            new TableRowCount("devices", 14),
            new TableRowCount("warranties", 16),
            new TableRowCount("customers", 8),
            new TableRowCount("addresses", 10),
            new TableRowCount("rma_requests", 1),
            new TableRowCount("rma_lines", 1),
            new TableRowCount("mcp_tool_calls", 240),
            new TableRowCount("operator_actions", 12),
            new TableRowCount("review_items", 3),
            new TableRowCount("webhook_events", 900),
            new TableRowCount("triage_refusals", 7),
        ]);

        Assert.Equal(
        [
            "  products:      5",
            "  devices:       14",
            "  warranties:    16",
            "  customers:     8",
            "  addresses:     10",
            "  rma_requests:  1",
            "  rma_lines:     1",
            string.Empty,
            "  mcp_tool_calls:   240",
            "  operator_actions: 12",
            "  review_items:     3",
            "  webhook_events:   900",
            "  triage_refusals:  7",
        ],
            lines);
    }

    [Fact]
    public async Task The_audit_counts_are_read_from_the_database_and_not_guessed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var seed = _database.CreateContext())
        {
            // Three of the five are left empty on purpose: a report that printed a constant, or
            // that answered from the wrong table, would still say something for each of them.
            seed.WebhookEvents.AddRange(
                new ProcessedWebhookEvent
                {
                    EventId = "evt_one",
                    Type = "payment_intent.succeeded",
                    ReceivedAtUtc = Now,
                },
                new ProcessedWebhookEvent
                {
                    EventId = "evt_two",
                    Type = "payment_intent.succeeded",
                    ReceivedAtUtc = Now,
                });

            seed.ReviewItems.Add(new ReviewItem
            {
                Id = Guid.NewGuid(),
                CreatedAtUtc = Now,
                Source = ReviewSources.PolicyHumanReview,
                Reason = "counted by stats",
                Status = ReviewStatuses.Open,
            });

            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var db = _database.CreateContext();
        var counts = (await StatsReport.ReadAsync(db, cancellationToken))
            .ToDictionary(count => count.Table, count => count.Rows);

        Assert.Equal(12, counts.Count);
        Assert.Equal(2, counts["webhook_events"]);
        Assert.Equal(1, counts["review_items"]);
        Assert.Equal(0, counts["mcp_tool_calls"]);
        Assert.Equal(0, counts["operator_actions"]);
        Assert.Equal(0, counts["triage_refusals"]);
    }

    /// <summary>
    /// The object a monitor scrapes. Pinned exactly, because the group names and the table keys are
    /// the interface: a flattened map or a renamed group breaks every collector reading it, and this
    /// is the only place that failure is cheap to find.
    /// </summary>
    [Fact]
    public void The_json_view_is_one_line_of_object_with_the_audit_tables_kept_apart()
    {
        var counts = new TableRowCount[]
        {
            new("products", 5),
            new("devices", 14),
            new("warranties", 16),
            new("customers", 8),
            new("addresses", 10),
            new("rma_requests", 1),
            new("rma_lines", 1),
            new("mcp_tool_calls", 240),
            new("operator_actions", 12),
            new("review_items", 3),
            new("webhook_events", 900),
            new("triage_refusals", 7),
        };

        var json = StatsReport.ToJson(counts);

        Assert.Equal(
            "{\"business\":{\"products\":5,\"devices\":14,\"warranties\":16,\"customers\":8,"
            + "\"addresses\":10,\"rma_requests\":1,\"rma_lines\":1},"
            + "\"audit\":{\"mcp_tool_calls\":240,\"operator_actions\":12,\"review_items\":3,"
            + "\"webhook_events\":900,\"triage_refusals\":7}}",
            json);

        // One record per run, so a line-based collector does not have to reassemble the document.
        Assert.DoesNotContain('\n', json);

        // An empty audit group is still an object: a monitor's path to its tables exists on the first
        // run of a fresh database, before anything has accumulated.
        Assert.Equal(
            "{\"business\":{\"products\":0},\"audit\":{}}",
            StatsReport.ToJson([new TableRowCount("products", 0)]));
    }

    /// <summary>
    /// The text and the object are two views of one list, not two lists: every table the table prints
    /// is a key in the object, in the same group, with the same number.
    /// </summary>
    [Fact]
    public void The_json_and_the_text_report_the_same_tables_and_numbers()
    {
        var counts = new TableRowCount[]
        {
            new("products", 5),
            new("customers", 8),
            new("rma_requests", 1),
            new("mcp_tool_calls", 240),
            new("webhook_events", 900),
        };

        using var document = JsonDocument.Parse(StatsReport.ToJson(counts));
        var business = document.RootElement.GetProperty("business");
        var audit = document.RootElement.GetProperty("audit");

        foreach (var count in counts)
        {
            var group = StatsReport.IsAudit(count.Table) ? audit : business;

            Assert.Equal(count.Rows, group.GetProperty(count.Table).GetInt32());
        }

        var printed = StatsReport.Format(counts)
            .Where(line => line.Length > 0)
            .Select(line => line.Split(':')[0].Trim())
            .ToList();

        Assert.Equal(counts.Length, printed.Count);

        foreach (var table in printed)
        {
            var group = StatsReport.IsAudit(table) ? audit : business;

            Assert.True(
                group.TryGetProperty(table, out _),
                $"{table} is a line in the text view but a key in neither group of the object");
        }
    }
}
