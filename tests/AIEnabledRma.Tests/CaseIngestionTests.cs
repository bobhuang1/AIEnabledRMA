using System.Net;
using System.Text;
using System.Text.Json;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Cases;
using AIEnabledRma.Domain.Knowledge;
using AIEnabledRma.Domain.Triage;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Cases;
using AIEnabledRma.Rag.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AIEnabledRma.Tests;

/// <summary>P6: CRM cases become troubleshooting knowledge without any customer identity.</summary>
public sealed class CaseIngestionTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    internal static (IReadOnlyList<SupportCase> Cases, IReadOnlyList<string> Planted) Corpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var planted = doc.RootElement.GetProperty("planted").EnumerateArray().Select(e => e.GetString()!).ToList();
        var cases = JsonSerializer.Deserialize<List<SupportCase>>(
            doc.RootElement.GetProperty("cases").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return (cases, planted);
    }

    internal static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-cases.json");

    private static CaseIngestionPipeline Pipeline() =>
        new(new PgVectorKnowledgeStore(NpgsqlDataSource.Create("Host=unused;Username=x;Password=placeholder"), new HashingEmbeddingModel()));

    [Fact]
    public void Prepared_cases_carry_no_planted_value_and_keep_the_troubleshooting_content()
    {
        var (cases, planted) = Corpus();

        var prepared = cases.Select(c => Pipeline().Prepare(c, "reviewer", Today)).ToList();
        var documents = prepared.Where(p => p.Document is not null).Select(p => p.Document!).ToList();
        var text = JsonSerializer.Serialize(documents) + JsonSerializer.Serialize(prepared.Select(p => p.Report));

        Assert.Equal(4, documents.Count);
        foreach (var value in planted)
        {
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        }

        var cradle = documents.Single(d => d.CitationLabel == "Resolved case (AX-200, 2026-08)");
        var all = string.Join("\n", cradle.Chunks.Select(c => c.Text));
        Assert.Contains("isopropyl", all, StringComparison.Ordinal);
        Assert.Contains("AX-200", all, StringComparison.Ordinal);
        Assert.Equal(["AX-200"], cradle.ProductScope);
        Assert.Contains(cradle.Chunks, c => c.HeadingPath.EndsWith("> Resolution", StringComparison.Ordinal));
        Assert.StartsWith("case-", cradle.SourceId, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolved_cases_are_not_eligible_and_a_case_with_a_surviving_identifier_is_quarantined()
    {
        var (cases, _) = Corpus();

        var reports = cases.Select(c => Pipeline().Prepare(c, "reviewer", Today).Report).ToList();

        Assert.Equal(CaseOutcomes.NotEligible, reports[4].Outcome);
        Assert.Equal(CaseOutcomes.Quarantined, reports[5].Outcome);
        Assert.Contains("DigitRun", reports[5].Residual);
    }

    [Fact]
    public void The_report_counts_redactions_by_kind()
    {
        var (cases, _) = Corpus();

        var report = Pipeline().Prepare(cases[0], "reviewer", Today).Report;

        Assert.True(report.Redactions["EMAIL"] >= 1);
        Assert.True(report.Redactions["NAME"] >= 1);
        Assert.True(report.Redactions.Where(r => r.Key is not "Signature" and not "Header").Sum(r => r.Value) >= 4);
        Assert.True(report.Redactions["Signature"] >= 1);
    }

    [Fact]
    public async Task Dataverse_requests_only_troubleshooting_columns_and_maps_resolved_incidents()
    {
        var handler = new RecordingHandler("""
            {"value":[{
              "incidentid":"11111111-2222-3333-4444-555555555555",
              "title":"Cradle not charging",
              "description":"Light stays amber.",
              "statecode":1,
              "modifiedon":"2026-08-14T10:05:00Z",
              "_productid_value@OData.Community.Display.V1.FormattedValue":"AX-200",
              "Incident_Resolutions":[{"description":"Cleaned the contacts.","actualend":"2026-08-14T10:00:00Z"}],
              "Incident_Annotation":[{"subject":"Check","notetext":"Contacts dirty."}]
            }]}
            """);
        var source = new DataverseCaseSource(
            new HttpClient(handler) { BaseAddress = new Uri("https://org.example.crm.dynamics.com/api/data/v9.2/") },
            new StaticToken());

        var cases = new List<SupportCase>();
        await foreach (var c in source.ReadModifiedSinceAsync(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None))
        {
            cases.Add(c);
        }

        var url = Uri.UnescapeDataString(handler.Requests.Single());
        foreach (var forbidden in new[] { "customerid", "_customerid_value", "contactid", "_primarycontactid_value", "_ownerid_value", "accountid", "emailaddress", "telephone", "_salesorderid" })
        {
            Assert.DoesNotContain(forbidden, url, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("modifiedon gt 2026-08-01T00:00:00Z", url, StringComparison.Ordinal);
        var mapped = Assert.Single(cases);
        Assert.True(mapped.IsResolvedWithResolution);
        Assert.Equal("AX-200", mapped.Product);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero), mapped.ClosedOn);
        Assert.Equal(["Check: Contacts dirty."], mapped.Notes);
    }

    private sealed class StaticToken : IDataverseTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult("synthetic-token");
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

/// <summary>
/// The P6 gate: a synthetic corpus with planted personal data, ingested end to end into a real
/// pgvector store, leaves zero planted values in chunks, documents, logs, the report or retrieval.
/// </summary>
public sealed class CaseLeakLiveTests : IAsyncLifetime
{
    private readonly LiveDatabase _db = new("rma_case_test");
    private NpgsqlDataSource? _dataSource;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _dataSource = NpgsqlDataSource.Create(_db.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Zero_planted_values_reach_the_store_the_logs_the_report_or_retrieval()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, planted) = CaseIngestionTests.Corpus();
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));

        var store = new PgVectorKnowledgeStore(_dataSource!, new HashingEmbeddingModel());
        await store.EnsureSchemaAsync(rebuild: false, ct);
        var pipeline = new CaseIngestionPipeline(store, logger: factory.CreateLogger<CaseIngestionPipeline>());

        var dryRun = await pipeline.RunAsync(new FileCaseSource(CaseIngestionTests.FixturePath), publish: false, "reviewer", new DateOnly(2026, 11, 20), ct);
        var published = await pipeline.RunAsync(new FileCaseSource(CaseIngestionTests.FixturePath), publish: true, "reviewer", new DateOnly(2026, 11, 20), ct);

        Assert.Null(dryRun.Published);
        Assert.Equal(1, dryRun.Quarantined);
        Assert.True(published.Published!.Activated);
        Assert.Equal(4, published.Published.Documents);

        var retriever = new HybridKnowledgeRetriever(
            store,
            new KnowledgeIndexRetriever(new KnowledgeIndexHolder
            {
                Current = new KnowledgeIndex(new Rag.Knowledge.KnowledgeBaseLoadResult(), Options.Create(new RagRetrievalOptions()), Options.Create(new RagOptions())),
            }),
            Options.Create(new VectorStoreOptions()),
            Options.Create(new RagOptions { EnabledProductFamilies = ["AX-200", "AX-400", "BX-100"] }),
            NullLogger<HybridKnowledgeRetriever>.Instance);

        var retrieved = new List<RetrievedArticle>();
        foreach (var question in new[] { "cradle not charging amber light", "scanner trigger stopped after a drop", "reboot loop after firmware update", "bluetooth range drops", "Dana Whitfield", "Harrison Street" })
        {
            var result = await retriever.RetrieveAsync(
                new KnowledgeRetrievalQuery { Query = question, ScopeTokens = new HashSet<string>(["AX-200", "AX-400", "BX-100"]), TopK = 5 },
                ct);
            retrieved.AddRange(result.Articles);
        }

        var stored = await DumpAsync(ct);
        var everything = string.Join(
            "\n",
            stored,
            JsonSerializer.Serialize(dryRun),
            JsonSerializer.Serialize(published),
            JsonSerializer.Serialize(retrieved),
            string.Join("\n", logs.Lines));

        foreach (var value in planted)
        {
            Assert.DoesNotContain(value, everything, StringComparison.OrdinalIgnoreCase);
        }

        // The knowledge is still there, cited as a resolved case and never by number.
        var cradle = retrieved.First(a => a.SourceKind == KbKinds.CrmCase && a.Snippet!.Contains("isopropyl", StringComparison.Ordinal));
        Assert.Equal("Resolved case (AX-200, 2026-08)", cradle.CitationLabel);

        // Incremental: a second publish with nothing new keeps the same content.
        var again = await pipeline.RunAsync(new FileCaseSource(CaseIngestionTests.FixturePath), publish: true, "reviewer", new DateOnly(2026, 11, 20), ct);
        Assert.Equal(0, again.Read);
        Assert.True(again.Published!.Unchanged);
    }

    private async Task<string> DumpAsync(CancellationToken ct)
    {
        var builder = new StringBuilder();
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        foreach (var sql in new[]
        {
            "SELECT source_id || ' ' || title || ' ' || coalesce(citation_label, '') || ' ' || array_to_string(product_scope, ',') FROM kb_documents",
            "SELECT heading_path || ' ' || text FROM kb_chunks",
            "SELECT key || ' ' || value FROM kb_meta",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                builder.AppendLine(reader.GetString(0));
            }
        }

        return builder.ToString();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Lines);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
