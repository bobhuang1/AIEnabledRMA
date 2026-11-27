using AIEnabledRma.Ai;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Domain.Triage;
using AIEnabledRma.Privacy;
using AIEnabledRma.Rag;
using AIEnabledRma.Tools.DbAdmin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// The refusal trail, written through the real sink into the migrated table.
/// </summary>
/// <remarks>
/// A refusal is the only moment the product learns that a customer asked something the corpus cannot
/// answer, and it used to leave nothing behind: the customer saw a canned sentence and the question
/// was gone. These tests exist because the row is only worth having if two things hold — it is
/// written where a report can read it, and it carries the statement the pipeline actually redacted
/// rather than the one the customer typed, which would put an email address in a table that exists
/// for a different purpose entirely.
/// </remarks>
public sealed class TriageRefusalTests : IAsyncLifetime
{
    private const string Email = "dana.whitfield@example.com";

    private const string OffTopicStatement = "my hinge sticks and I cannot open the lid";

    private readonly LiveDatabase _database = new("rma_refusal_test");

    public async ValueTask InitializeAsync() => await _database.InitializeAsync();

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task A_refused_question_is_stored_with_the_statement_the_pipeline_redacted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await MigrateAsync(cancellationToken);

        await using (var write = _database.CreateContext())
        {
            var pipeline = Build(new EfTriageRefusalLog(write), out _);

            var result = await pipeline.TriageAsync(
                Request($"{OffTopicStatement} {Email}"),
                cancellationToken);

            Assert.True(result.RefusedByScopeGuard);
        }

        // A second context, so the assertion is about what PostgreSQL holds rather than what the
        // change tracker still remembers.
        await using var read = _database.CreateContext();
        var row = Assert.Single(await read.TriageRefusals.AsNoTracking().ToListAsync(cancellationToken));

        Assert.False(
            string.IsNullOrWhiteSpace(row.Reason),
            "the row has to say why the guard refused, or a corpus review cannot tell a vocabulary gap from an off-topic question");
        Assert.DoesNotContain(Email, row.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[EMAIL_1]", row.Statement, StringComparison.Ordinal);
        Assert.StartsWith(OffTopicStatement, row.Statement, StringComparison.Ordinal);

        // The row says when it happened, because the report is read as a window and a refusal with
        // no time would be an undated corpus gap.
        Assert.True(
            (DateTimeOffset.UtcNow - row.AtUtc).Duration() < TimeSpan.FromMinutes(5),
            $"the refusal is stamped {row.AtUtc:O}, which is not when the test ran");
    }

    /// <summary>
    /// The stamp comes from the same seam as every other date in the flow, so a refusal can be put
    /// in the past and read through the report's window without a test waiting for one. The wall
    /// clock made the row's date the only one in the system a test could not choose: a fact about
    /// the window had to seed its own rows rather than produce them through the pipeline.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_stamped_by_the_clock_the_flow_reads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await MigrateAsync(cancellationToken);

        // Midnight, thirty days back: inside a sixty-day window and outside a seven-day one, and
        // exact to the second, so the assertion is about the instant rather than about precision.
        var stampedAt = new DateTimeOffset(
            DateTimeOffset.UtcNow.AddDays(-30).UtcDateTime.Date,
            TimeSpan.Zero);

        await using (var write = _database.CreateContext())
        {
            var pipeline = Build(new EfTriageRefusalLog(write), out _, new FakeClock(stampedAt));

            var result = await pipeline.TriageAsync(
                Request(OffTopicStatement),
                cancellationToken);

            Assert.True(result.RefusedByScopeGuard);
        }

        await using var read = _database.CreateContext();
        var row = Assert.Single(await read.TriageRefusals.AsNoTracking().ToListAsync(cancellationToken));

        // The pipeline's own row, dated by the clock it was given, not by the machine's.
        Assert.Equal(stampedAt, row.AtUtc);

        // The window reads the same instant back, so "refused this week" and "refused this quarter"
        // are two queries against one row rather than two waits.
        var week = await TriageRefusalReport.ReadAsync(
            read, DateTimeOffset.UtcNow.AddDays(-7), 10, cancellationToken);
        Assert.Equal(0, week.Refusals);

        var quarter = await TriageRefusalReport.ReadAsync(
            read, DateTimeOffset.UtcNow.AddDays(-60), 10, cancellationToken);
        Assert.Equal(1, quarter.Refusals);
    }

    [Fact]
    public async Task An_answered_question_records_no_refusal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await MigrateAsync(cancellationToken);

        await using var db = _database.CreateContext();
        var pipeline = Build(new EfTriageRefusalLog(db), out _);

        var result = await pipeline.TriageAsync(
            Request("the indicator is dark and it will not power on"),
            cancellationToken);

        Assert.False(result.RefusedByScopeGuard);
        Assert.Empty(await db.TriageRefusals.AsNoTracking().ToListAsync(cancellationToken));
    }

    /// <summary>
    /// The wiring the row depends on. <see cref="TriagePipeline"/> takes its log as an optional
    /// constructor argument, and the container supplies that default when no sink is registered —
    /// so a host that lost the registration still starts, still refuses, and still answers the
    /// customer, and the only observable difference is the row that never appears.
    /// </summary>
    /// <remarks>
    /// The pipeline's own facts cannot catch that: they pass a log in. This one resolves the
    /// pipeline from the booted Web host, so the sink under test is the registration
    /// <c>Program</c> makes and nothing else, and the refusal has to travel the whole way through
    /// the shipped guard and corpus to produce the row.
    /// </remarks>
    [Fact]
    public async Task The_web_host_writes_the_refusal_through_the_sink_it_registers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await MigrateAsync(cancellationToken);

        const string statement = "write me a poem about my hinge";

        using var factory = new RefusalHostFactory(_database.ConnectionString);
        using var client = factory.CreateClient();

        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider
                .GetRequiredService<ITriageService>()
                .TriageAsync(Request(statement), cancellationToken);

            Assert.True(result.RefusedByScopeGuard);
        }

        // A fresh context, so the row is the one the host's sink committed rather than anything
        // this process still has in a change tracker.
        await using var read = _database.CreateContext();
        var rows = await read.TriageRefusals.AsNoTracking().ToListAsync(cancellationToken);

        Assert.True(
            rows.Count == 1,
            $"{rows.Count} refusal row(s) in triage_refusals. A pipeline whose log is not registered still "
            + "refuses and still answers the customer, so only the row tells a host that records refusals "
            + "from one that drops them.");

        var row = rows[0];
        Assert.False(
            string.IsNullOrWhiteSpace(row.Reason),
            "the host's row has to carry the guard's reason, or the report cannot say why the question was refused");
        Assert.Equal(statement, row.Statement);
    }

    /// <summary>
    /// The row is evidence, not a dependency: a database that is down must not turn a refusal into
    /// an error page, because the customer's answer has already been decided.
    /// </summary>
    [Fact]
    public async Task A_refusal_survives_a_log_that_cannot_be_written()
    {
        var pipeline = Build(new ThrowingRefusalLog(), out _);

        var result = await pipeline.TriageAsync(
            Request($"{OffTopicStatement} {Email}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.RefusedByScopeGuard);
        Assert.Equal(0d, result.Verdict.Confidence);
    }

    [Fact]
    public async Task The_report_ranks_what_was_asked_most_and_counts_the_whole_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await MigrateAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        await using (var seed = _database.CreateContext())
        {
            seed.TriageRefusals.AddRange(
                Refusal(now.AddDays(-1), "off-topic", OffTopicStatement),
                Refusal(now.AddDays(-2), "off-topic", OffTopicStatement),
                Refusal(now.AddDays(-3), "not-this-item", "the app crashes when I scan a barcode"),
                // Outside the window: counted by nothing, so the report cannot be read as "three".
                Refusal(now.AddDays(-40), "off-topic", "an old question nobody acted on"));
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var read = _database.CreateContext();

        var digest = await TriageRefusalReport.ReadAsync(read, now.AddDays(-7), take: 10, cancellationToken);

        Assert.Equal(3, digest.Refusals);
        Assert.False(digest.More);
        Assert.Equal(2, digest.Rows.Count);

        var first = digest.Rows[0];
        Assert.Equal(2, first.Count);
        Assert.Equal(OffTopicStatement, first.Statement);
        Assert.Equal("off-topic", first.Reason);

        // The line an operator reads has to carry the number, which is the ranking's whole point.
        Assert.Contains(
            "   2x",
            string.Join('\n', TriageRefusalReport.Format(digest)),
            StringComparison.Ordinal);
        Assert.Contains(
            "3 refusal(s)",
            TriageRefusalReport.Summary(digest),
            StringComparison.Ordinal);

        // A bound shorter than the ranking says so rather than printing a shorter list as if it
        // were the whole picture.
        var bounded = await TriageRefusalReport.ReadAsync(read, now.AddDays(-7), take: 1, cancellationToken);
        Assert.True(bounded.More);
        Assert.Single(bounded.Rows);
        Assert.Equal(3, bounded.Refusals);
        Assert.Contains("raise --take", TriageRefusalReport.Summary(bounded), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_window_says_so_instead_of_printing_a_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await MigrateAsync(cancellationToken);

        await using var db = _database.CreateContext();
        var digest = await TriageRefusalReport.ReadAsync(db, DateTimeOffset.UtcNow.AddDays(-7), 10, cancellationToken);

        Assert.Empty(digest.Rows);
        Assert.Equal(0, digest.Refusals);
        Assert.Contains("(none)", string.Join('\n', TriageRefusalReport.Format(digest)), StringComparison.Ordinal);
        Assert.Equal("Nothing was refused in this window.", TriageRefusalReport.Summary(digest));
    }

    private static TriageRefusal Refusal(DateTimeOffset at, string reason, string statement) => new()
    {
        Id = Guid.NewGuid(),
        AtUtc = at,
        Reason = reason,
        Statement = statement,
    };

    private sealed class ThrowingRefusalLog : ITriageRefusalLog
    {
        public Task RecordAsync(TriageRefusal refusal, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the database is down");
    }

    /// <summary>
    /// The Web host, booted against the throwaway database, so the pipeline it builds is the one a
    /// deployment builds. Nothing here registers a refusal sink: the fact is about the registration
    /// <c>Program</c> makes, and a sink supplied by the fixture would pass whatever the host did.
    /// </summary>
    private sealed class RefusalHostFactory(string connectionString)
        : WebApplicationFactory<global::WebAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Rma", connectionString);

            // The checked-in configuration carries a placeholder connection, so the host would
            // start and fail on its first query rather than here.
            builder.UseEnvironment("Testing");

            // The bundled accept-everything gateway refuses to register outside Development, and
            // the host registers it whether or not a test exercises it.
            builder.UseSetting(
                $"{FakePaymentGatewayOptions.SectionName}:{nameof(FakePaymentGatewayOptions.AllowInProduction)}",
                "true");

            // Testing is outside Development, where a host with no operator key refuses to start:
            // /health's detail would be open to every caller behind a proxy.
            builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, "refusal-tests-operator-key");
        }
    }

    private static TriageRequest Request(string statement) => new()
    {
        Devices =
        [
            new TriageDeviceContext
            {
                SerialNumber = "SN-1",
                ProductName = "Vertex Widget",
                Model = "VW-100",
                Sku = "VW-100-BLK",
                ProductCategory = "power",
                IsInWarranty = true,
                ScopeTokens = new HashSet<string>(["Vertex Widget"], StringComparer.OrdinalIgnoreCase),
            },
        ],
        Conversation = [],
        CustomerStatement = statement,
        CorrelationId = "refusal-test",
    };

    /// <summary>
    /// The pipeline with its real guard and redactor, a model that always answers the same verdict,
    /// and the log under test.
    /// </summary>
    private static TriagePipeline Build(
        ITriageRefusalLog log,
        out StubChatModel model,
        IClock? clock = null)
    {
        model = new StubChatModel(new ChatCompletion
        {
            Text = """
            {
              "resolved": false,
              "confidence": 0.8,
              "summary": "The supply is not reaching the unit.",
              "steps": [ { "instruction": "Reseat the connector.", "article_id": "kb-0001" } ],
              "article_ids": ["kb-0001"]
            }
            """,
        });

        var retriever = new FakeRetriever
        {
            Articles =
            [
                new RetrievedArticle
                {
                    Id = "kb-0001",
                    Title = "Unit will not power on",
                    Category = "power",
                    Score = 0.9,
                    Snippet = "Check the indicator and the supply.",
                },
            ],
        };

        var guard = new ScopeGuard(
            new ScopeGuardOptions(),
            [["indicator", "power", "dark", "light", "unit"]]);

        return new TriagePipeline(
            model,
            retriever,
            guard,
            new PiiRedactor(),
            Options.Create(new RmaPolicyOptions { HumanReviewConfidenceThreshold = 0.45 }),
            Options.Create(new TriagePipelineOptions()),
            clock ?? new FakeClock(DateTimeOffset.UtcNow),
            NullLogger<TriagePipeline>.Instance,
            log);
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var migration = _database.CreateContext();
        await migration.Database.MigrateAsync(cancellationToken);
    }
}
