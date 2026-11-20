using System.Net;
using System.Text;
using AIEnabledRma.Ai;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Knowledge;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Knowledge;
using AIEnabledRma.Rag.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AIEnabledRma.Tests;

/// <summary>D4: the embedding model is a configuration choice, Ollama by default.</summary>
public sealed class EmbeddingProviderTests
{
    private static IEmbeddingModel Resolve(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmbeddingModel(configuration);
        return services.BuildServiceProvider(validateScopes: true).GetRequiredService<IEmbeddingModel>();
    }

    [Fact]
    public void Ollama_is_the_default_with_nomic_embed_text()
    {
        var model = Resolve();

        Assert.IsType<OllamaEmbeddingModel>(model);
        Assert.Equal("ollama:nomic-embed-text", model.Name);
        Assert.Equal(768, model.Dimensions);
    }

    [Fact]
    public void Each_provider_is_selected_by_configuration()
    {
        var ollama = Resolve(("Embeddings:Provider", "ollama"), ("Embeddings:Model", "bge-m3"), ("Embeddings:Dimensions", "1024"));
        var hosted = Resolve(
            ("Embeddings:Provider", "openai-compatible"),
            ("Embeddings:BaseUrl", "https://embeddings.example.com/v1"),
            ("Embeddings:Model", "text-embedding-3-small"),
            ("Embeddings:Dimensions", "1536"));
        var hashing = Resolve(("Embeddings:Provider", "hashing"), ("Embeddings:Dimensions", "384"));

        Assert.Equal("ollama:bge-m3", ollama.Name);
        Assert.Equal(1024, ollama.Dimensions);
        Assert.IsType<OpenAiCompatibleEmbeddingModel>(hosted);
        Assert.Equal("openai-compatible:text-embedding-3-small", hosted.Name);
        Assert.IsType<HashingEmbeddingModel>(hashing);
        Assert.Equal(384, hashing.Dimensions);
    }

    [Fact]
    public void An_unknown_provider_or_a_hosted_provider_without_a_url_fails_validation()
    {
        Assert.ThrowsAny<OptionsValidationException>(() =>
            new ServiceCollection().AddLogging().AddEmbeddingModel(Config(("Embeddings:Provider", "magic")))
                .BuildServiceProvider().GetRequiredService<IOptions<EmbeddingOptions>>().Value);
        Assert.ThrowsAny<OptionsValidationException>(() =>
            new ServiceCollection().AddLogging().AddEmbeddingModel(Config(("Embeddings:Provider", "openai-compatible")))
                .BuildServiceProvider().GetRequiredService<IOptions<EmbeddingOptions>>().Value);
    }

    [Fact]
    public async Task Ollama_is_called_on_api_embed_in_batches()
    {
        var handler = new Respond(texts => "{\"embeddings\":[" + string.Join(",", texts.Select(_ => "[0.1,0.2,0.3]")) + "]}");
        var model = new OllamaEmbeddingModel(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") },
            Options.Create(new EmbeddingOptions { Model = "nomic-embed-text", Dimensions = 3, BatchSize = 2 }),
            NullLogger<OllamaEmbeddingModel>.Instance);

        var vectors = await model.EmbedAsync(["a", "b", "c"], CancellationToken.None);

        Assert.Equal(3, vectors.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("/api/embed", r.Path));
        Assert.Contains("\"model\":\"nomic-embed-text\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vectors_of_the_wrong_size_are_refused()
    {
        var handler = new Respond(texts => "{\"embeddings\":[" + string.Join(",", texts.Select(_ => "[0.1,0.2]")) + "]}");
        var model = new OllamaEmbeddingModel(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") },
            Options.Create(new EmbeddingOptions { Model = "bge-m3", Dimensions = 1024 }),
            NullLogger<OllamaEmbeddingModel>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => model.EmbedAsync(["a"], CancellationToken.None));

        Assert.Contains("Dimensions", ex.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    private sealed class Respond(Func<IReadOnlyList<string>, string> body) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, text));
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var inputs = doc.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(inputs), Encoding.UTF8, "application/json") };
        }
    }
}

/// <summary>D4: the store never mixes vectors from different models or dimensions.</summary>
public sealed class EmbeddingMismatchLiveTests : IAsyncLifetime
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

        _database = $"rma_emb_test_{Guid.NewGuid():N}";
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

    [Fact]
    public async Task Another_model_or_dimension_is_refused_until_a_rebuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var loaded = new KnowledgeBaseLoader(NullLogger<KnowledgeBaseLoader>.Instance)
            .Load(RagServiceCollectionExtensions.ResolveKnowledgeBaseRoot("knowledge-base"));
        var documents = ArticleDocuments.From(loaded);

        var original = new PgVectorKnowledgeStore(_dataSource!, new HashingEmbeddingModel(384));
        await original.EnsureSchemaAsync(rebuild: false, ct);
        await original.IngestAsync(KbKinds.Article, documents, ct);

        // Same provider, different dimension; and a different model name of the same size.
        var resized = new PgVectorKnowledgeStore(_dataSource!, new HashingEmbeddingModel(256));
        var renamed = new PgVectorKnowledgeStore(_dataSource!, new RenamedModel(new HashingEmbeddingModel(384)));

        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => resized.EnsureSchemaAsync(rebuild: false, ct));
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => resized.IngestAsync(KbKinds.Article, documents, ct));
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => renamed.SearchAsync("screen is black", [], 5, ct));

        // Every stored chunk records its model.
        await using (var connection = await _dataSource!.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT DISTINCT embedding_model FROM kb_chunks", connection))
        {
            Assert.Equal("hashing-v1-384", (string?)await cmd.ExecuteScalarAsync(ct));
        }

        // Retrieval with the wrong model falls back to the reviewed lexical index instead of
        // comparing vectors from different spaces.
        var ragOptions = Options.Create(new RagOptions());
        var lexical = new KnowledgeIndexRetriever(new KnowledgeIndexHolder
        {
            Current = new KnowledgeIndex(loaded, Options.Create(new RagRetrievalOptions()), ragOptions),
        });
        var hybrid = new HybridKnowledgeRetriever(renamed, lexical, Options.Create(new VectorStoreOptions()), ragOptions, NullLogger<HybridKnowledgeRetriever>.Instance);
        var result = await hybrid.RetrieveAsync(
            new KnowledgeRetrievalQuery { Query = "screen stays black", ScopeTokens = new HashSet<string>(), TopK = 3 }, ct);
        Assert.Contains(result.Articles, a => a.Id == "kb-0004");
        Assert.All(result.Articles, a => Assert.Null(a.ChunkRef));

        // A rebuild re-embeds with the new model.
        await resized.EnsureSchemaAsync(rebuild: true, ct);
        var rebuilt = await resized.IngestAsync(KbKinds.Article, documents, ct);
        Assert.True(rebuilt.Activated);
        Assert.Equal(rebuilt.Chunks, rebuilt.Embedded);
    }

    private sealed class RenamedModel(IEmbeddingModel inner) : IEmbeddingModel
    {
        public string Name => "ollama:bge-m3";

        public int Dimensions => inner.Dimensions;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            inner.EmbedAsync(texts, cancellationToken);
    }
}
