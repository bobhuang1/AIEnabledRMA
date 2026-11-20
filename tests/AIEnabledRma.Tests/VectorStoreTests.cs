using System.Text.Json;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Knowledge;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Knowledge;
using AIEnabledRma.Rag.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AIEnabledRma.Tests;

/// <summary>P5: structure-aware chunking.</summary>
public sealed class MarkdownChunkerTests
{
    [Fact]
    public void Headings_become_the_chunk_path_with_the_title_first()
    {
        var chunks = MarkdownChunker.Chunk(
            "Unit will not power on",
            "# Unit will not power on\n\n## Check the battery\n\n" + Words(80) + "\n\n## Hard reset\n\n" + Words(80),
            new ChunkingOptions());

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Unit will not power on > Check the battery", chunks[0].HeadingPath);
        Assert.Equal("Unit will not power on > Hard reset", chunks[1].HeadingPath);
        Assert.StartsWith("Unit will not power on > Hard reset\n\n", chunks[1].IndexText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_numbered_list_of_steps_is_never_split()
    {
        var steps = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"{i}. Step {i}: " + Words(25)));
        var options = new ChunkingOptions { TargetTokens = 100, MaxTokens = 260, MinTokens = 10 };

        var chunks = MarkdownChunker.Chunk("Steps", Words(60) + "\n\n" + steps + "\n\n" + Words(60), options);

        var holding = Assert.Single(chunks, c => c.Text.Contains("1. Step 1:", StringComparison.Ordinal));
        Assert.Contains("12. Step 12:", holding.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_section_is_split_under_the_maximum_with_overlap()
    {
        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"Paragraph {i}. " + Words(45)));
        var options = new ChunkingOptions { TargetTokens = 200, MaxTokens = 250, MinTokens = 10, OverlapFraction = 0.3 };

        var chunks = MarkdownChunker.Chunk("Long", paragraphs, options);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= options.MaxTokens + 70, $"chunk of {c.TokenCount} tokens"));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void A_tiny_section_is_merged_into_the_next_one()
    {
        var chunks = MarkdownChunker.Chunk("T", "## Intro\n\nShort.\n\n## Detail\n\n" + Words(90), new ChunkingOptions());

        var chunk = Assert.Single(chunks);
        Assert.Contains("Short.", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Detail:", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_shipped_article_chunks_within_bounds()
    {
        var loaded = new KnowledgeBaseLoader(NullLogger<KnowledgeBaseLoader>.Instance)
            .Load(RagServiceCollectionExtensions.ResolveKnowledgeBaseRoot("knowledge-base"));
        var options = new ChunkingOptions();

        foreach (var article in loaded.Articles)
        {
            var chunks = MarkdownChunker.Chunk(article.Title, article.Body, options);
            Assert.NotEmpty(chunks);
            Assert.All(chunks, c => Assert.True(c.TokenCount <= options.MaxTokens * 2, $"{article.Id} chunk {c.Ordinal} is {c.TokenCount} tokens"));
        }
    }

    internal static string Words(int count) =>
        string.Join(' ', Enumerable.Range(0, count).Select(i => "word" + (i % 17)));
}

public sealed class HashingEmbeddingModelTests
{
    [Fact]
    public async Task Vectors_are_deterministic_normalised_and_closer_for_related_text()
    {
        var model = new HashingEmbeddingModel(256);
        var vectors = await model.EmbedAsync(
            ["the cradle is not charging the unit", "unit not charging in the cradle", "bluetooth pairing drops"],
            CancellationToken.None);
        var again = await model.EmbedAsync(["the cradle is not charging the unit"], CancellationToken.None);

        Assert.Equal(vectors[0], again[0]);
        Assert.Equal(1d, Math.Sqrt(vectors[0].Sum(v => (double)v * v)), 3);
        Assert.True(Cosine(vectors[0], vectors[1]) > Cosine(vectors[0], vectors[2]));
    }

    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();
}

/// <summary>
/// P5 against a real PostgreSQL with pgvector (the CI service image, or the throwaway local
/// container). Each test gets its own database.
/// </summary>
public sealed class VectorStoreLiveTests : IAsyncLifetime
{
    private string _admin = string.Empty;
    private string _database = string.Empty;
    private NpgsqlDataSource? _dataSource;

    public async ValueTask InitializeAsync()
    {
        _admin = Environment.GetEnvironmentVariable(LiveDatabase.ConnectionVariable)
            ?? "Host=localhost;Port=5432;Database=postgres;Username=rmauser;Password=placeholder";

        try
        {
            await using var probe = new NpgsqlConnection(_admin);
            await probe.OpenAsync();
        }
        catch (Exception ex)
        {
            LiveDatabase.Unavailable(ex);
        }

        _database = $"rma_kb_test_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlConnection(_admin))
        {
            await create.OpenAsync();
            await using var cmd = create.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{_database}\"";
            cmd.CommandTimeout = LiveDatabase.DdlTimeoutSeconds;
            await cmd.ExecuteNonQueryAsync();
        }

        _dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_admin) { Database = _database }.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_database.Length == 0)
        {
            return;
        }

        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlConnection(_admin);
            await drop.OpenAsync();
            await using var cmd = drop.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)";
            cmd.CommandTimeout = LiveDatabase.DdlTimeoutSeconds;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] EnabledFamilies = ["Vertex Widget", "Nimbus Beacon"];

    private static KnowledgeBaseLoadResult ShippedArticles() =>
        new KnowledgeBaseLoader(NullLogger<KnowledgeBaseLoader>.Instance)
            .Load(RagServiceCollectionExtensions.ResolveKnowledgeBaseRoot("knowledge-base"), new KnowledgeIngestionOptions { RequireReviewed = true });

    private async Task<PgVectorKnowledgeStore> StoreAsync(IEmbeddingModel? embedder = null)
    {
        var store = new PgVectorKnowledgeStore(_dataSource!, embedder ?? new HashingEmbeddingModel());
        await store.EnsureSchemaAsync(rebuild: false, Ct);
        return store;
    }

    [Fact]
    public async Task Ingest_is_idempotent_and_reuses_vectors_for_unchanged_chunks()
    {
        var store = await StoreAsync();
        var documents = ArticleDocuments.From(ShippedArticles());

        var first = await store.IngestAsync(KbKinds.Article, documents, Ct);
        var second = await store.IngestAsync(KbKinds.Article, documents, Ct);

        Assert.True(first.Activated);
        Assert.Equal(first.Chunks, first.Embedded);
        Assert.True(second.Unchanged);

        // One article edited: only its chunks are embedded again.
        var edited = documents
            .Select(d => d.SourceId == "kb-0001"
                ? d with
                {
                    ContentHash = "changed",
                    Chunks = [.. d.Chunks.Take(d.Chunks.Count - 1), d.Chunks[^1] with { Text = d.Chunks[^1].Text + " Also check the fuse." }],
                }
                : d)
            .ToList();
        var third = await store.IngestAsync(KbKinds.Article, edited, Ct);

        Assert.True(third.Activated);
        Assert.Equal(1, third.Embedded);
        Assert.Equal(third.Chunks - 1, third.Reused);
        Assert.Equal(1, await ScalarAsync("SELECT count(DISTINCT generation) FROM kb_documents"));
    }

    [Fact]
    public async Task A_failed_embedding_keeps_the_previous_generation_and_is_retried()
    {
        var good = new HashingEmbeddingModel();
        var documents = ArticleDocuments.From(ShippedArticles());
        var first = await (await StoreAsync(good)).IngestAsync(KbKinds.Article, documents, Ct);

        var changed = documents.Select(d => d.SourceId == "kb-0004"
            ? d with { ContentHash = "v2", Chunks = [.. d.Chunks.Select(c => c with { Text = c.Text + " FAILME" })] }
            : d).ToList();

        var failing = new FailingEmbeddingModel(good, "FAILME");
        var attempt = await new PgVectorKnowledgeStore(_dataSource!, failing).IngestAsync(KbKinds.Article, changed, Ct);

        Assert.False(attempt.Activated);
        Assert.True(attempt.Failed > 0);
        Assert.Equal(first.Generation, await ScalarAsync("SELECT value::int FROM kb_meta WHERE key = 'active_generation:article'"));
        Assert.True(await ScalarAsync("SELECT count(*) FROM kb_chunks WHERE embedding_status = 'failed'") > 0);

        var retry = await new PgVectorKnowledgeStore(_dataSource!, good).IngestAsync(KbKinds.Article, changed, Ct);

        Assert.True(retry.Activated);
        Assert.Equal(0, retry.Failed);
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM kb_chunks WHERE embedding_status <> 'ok'"));
    }

    [Fact]
    public async Task Scoped_documents_are_filtered_in_sql_before_ranking()
    {
        var store = await StoreAsync();
        await store.IngestAsync(KbKinds.Article, ArticleDocuments.From(ShippedArticles()), Ct);
        const string question = "the multi-slot cradle charges one unit but the second unit does not charge";

        var universalOnly = await store.SearchAsync(question, [], 30, Ct);
        var scoped = await store.SearchAsync(question, ["Vertex Widget"], 30, Ct);

        Assert.DoesNotContain(universalOnly, h => h.SourceId == "kb-0018");
        Assert.Contains(scoped, h => h.SourceId == "kb-0018");
        Assert.DoesNotContain(scoped, h => h.SourceId == "kb-0019");
    }

    [Fact]
    public async Task Hybrid_recall_at_3_is_not_worse_than_the_lexical_index_on_the_golden_set()
    {
        var store = await StoreAsync();
        var loaded = ShippedArticles();
        await store.IngestAsync(KbKinds.Article, ArticleDocuments.From(loaded), Ct);

        var ragOptions = Options.Create(new RagOptions { EnabledProductFamilies = [.. EnabledFamilies] });
        var lexical = new KnowledgeIndexRetriever(new KnowledgeIndexHolder
        {
            Current = new KnowledgeIndex(loaded, Options.Create(new RagRetrievalOptions()), ragOptions),
        });
        var hybrid = new HybridKnowledgeRetriever(
            store,
            lexical,
            Options.Create(new VectorStoreOptions()),
            ragOptions,
            NullLogger<HybridKnowledgeRetriever>.Instance);

        var lexicalRecall = await GoldenSet.RecallAtAsync(lexical, 3, Ct);
        var hybridRecall = await GoldenSet.RecallAtAsync(hybrid, 3, Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"recall@3 lexical={lexicalRecall:0.000} hybrid={hybridRecall:0.000}");
        Assert.True(hybridRecall >= lexicalRecall, $"hybrid recall@3 {hybridRecall:0.000} < lexical {lexicalRecall:0.000}");
        Assert.True(hybridRecall >= 0.8, $"hybrid recall@3 {hybridRecall:0.000}");
    }

    [Fact]
    public async Task An_unreachable_store_falls_back_to_the_in_memory_index()
    {
        var loaded = ShippedArticles();
        var ragOptions = Options.Create(new RagOptions());
        var lexical = new KnowledgeIndexRetriever(new KnowledgeIndexHolder
        {
            Current = new KnowledgeIndex(loaded, Options.Create(new RagRetrievalOptions()), ragOptions),
        });
        await using var nowhere = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=placeholder;Timeout=2");
        var hybrid = new HybridKnowledgeRetriever(
            new PgVectorKnowledgeStore(nowhere, new HashingEmbeddingModel()),
            lexical,
            Options.Create(new VectorStoreOptions()),
            ragOptions,
            NullLogger<HybridKnowledgeRetriever>.Instance);

        var result = await hybrid.RetrieveAsync(
            new KnowledgeRetrievalQuery { Query = "screen stays black", ScopeTokens = new HashSet<string>(), TopK = 3 },
            Ct);

        Assert.Contains(result.Articles, a => a.Id == "kb-0004");
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, connection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FailingEmbeddingModel(IEmbeddingModel inner, string marker) : IEmbeddingModel
    {
        public string Name => inner.Name;

        public int Dimensions => inner.Dimensions;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            texts.Any(t => t.Contains(marker, StringComparison.Ordinal))
                ? throw new HttpRequestException("synthetic embedding outage")
                : inner.EmbedAsync(texts, cancellationToken);
    }
}

/// <summary>The golden question set shared by the retrieval tests and the evaluation (P7).</summary>
internal static class GoldenSet
{
    internal sealed record Question(string Id, string QuestionText, string[] Expected, string[] Scope, string Class);

    public static IReadOnlyList<Question> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-questions.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("questions").EnumerateArray()
            .Select(q => new Question(
                q.GetProperty("id").GetString()!,
                q.GetProperty("question").GetString()!,
                q.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                q.TryGetProperty("scope", out var scope) ? scope.EnumerateArray().Select(e => e.GetString()!).ToArray() : [],
                q.GetProperty("class").GetString()!))
            .ToList();
    }

    public static async Task<double> RecallAtAsync(IKnowledgeRetriever retriever, int k, CancellationToken ct)
    {
        var questions = Load().Where(q => q.Expected.Length > 0).ToList();
        var hits = 0;
        foreach (var q in questions)
        {
            var result = await retriever.RetrieveAsync(
                new KnowledgeRetrievalQuery
                {
                    Query = q.QuestionText,
                    ScopeTokens = q.Scope.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    TopK = k,
                },
                ct);
            if (result.Articles.Take(k).Any(a => q.Expected.Contains(a.Id, StringComparer.OrdinalIgnoreCase)))
            {
                hits++;
            }
        }

        return (double)hits / questions.Count;
    }
}
