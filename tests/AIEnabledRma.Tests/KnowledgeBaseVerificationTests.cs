using System.Security.Cryptography;
using System.Text;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// The startup check is what turns "the corpus ships with the image" into "the host loaded the
/// reviewed corpus": it compares what is on disk with <c>REVIEWED.sha256</c>, logs how many articles
/// it found, and refuses to start outside Development when the two disagree.
/// </summary>
/// <remarks>
/// Both silent failures this repository has had were invisible from the host: an image with no corpus
/// at all, and a publish that carried no manifest. The loader recorded those as errors only when the
/// environment forced review, so a container in Development answered from an empty or drifted corpus
/// without anything in the log saying so.
/// </remarks>
public sealed class KnowledgeBaseVerificationTests
{
    [Fact]
    public void A_corpus_that_matches_its_manifest_has_no_problems()
    {
        using var corpus = new Corpus();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001");
        corpus.Article("display/kb-0002-flicker.md", "kb-0002");
        corpus.Manifest();

        var verification = KnowledgeBaseVerifier.Verify(corpus.Load());

        Assert.True(verification.Matches, string.Join(" | ", verification.Problems));
        Assert.Equal(2, verification.Articles);
        Assert.Equal(2, verification.ManifestEntries);
    }

    [Fact]
    public void An_article_edited_after_review_is_a_problem()
    {
        using var corpus = new Corpus();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001");
        corpus.Manifest();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001", body: "edited after the hash was recorded");

        var verification = KnowledgeBaseVerifier.Verify(corpus.Load());

        Assert.False(verification.Matches);
        Assert.Contains(verification.Problems, problem => problem.Contains("changed after review", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_manifest_is_a_problem()
    {
        using var corpus = new Corpus();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001");

        var verification = KnowledgeBaseVerifier.Verify(corpus.Load());

        Assert.False(verification.Matches);
        Assert.Contains(verification.Problems, problem => problem.Contains("REVIEWED.sha256 is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void An_article_with_no_manifest_line_is_named()
    {
        using var corpus = new Corpus();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001");
        corpus.Manifest();
        corpus.Article("connectivity/kb-0002-unlisted.md", "kb-0002");

        var verification = KnowledgeBaseVerifier.Verify(corpus.Load());

        Assert.Contains(
            verification.Problems,
            problem => problem.Contains("connectivity/kb-0002-unlisted.md has no line", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manifest_line_for_a_file_that_is_not_there_is_named()
    {
        using var corpus = new Corpus();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001");
        corpus.Manifest(extra: "connectivity/kb-9999-deleted.md");

        var verification = KnowledgeBaseVerifier.Verify(corpus.Load());

        Assert.Contains(
            verification.Problems,
            problem => problem.Contains("lists connectivity/kb-9999-deleted.md", StringComparison.Ordinal));
    }

    /// <summary>
    /// The empty-corpus case: a manifest with lines and no articles is as broken as no manifest at
    /// all, and "0 articles" is the number an operator has to be able to see.
    /// </summary>
    [Fact]
    public void An_empty_corpus_reports_zero_articles()
    {
        using var corpus = new Corpus();
        corpus.Manifest(extra: "connectivity/kb-0001-pairing.md");

        var verification = KnowledgeBaseVerifier.Verify(corpus.Load());

        Assert.Equal(0, verification.Articles);
        Assert.False(verification.Matches);
    }

    [Fact]
    public async Task Outside_development_a_drifted_corpus_stops_startup()
    {
        using var corpus = new Corpus();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001");
        corpus.Manifest();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001", body: "edited after review");

        var check = corpus.Check("Production");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));

        Assert.Contains("changed after review", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("refuses to start", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_development_the_same_corpus_starts_and_is_logged()
    {
        using var corpus = new Corpus();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001");
        corpus.Manifest();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001", body: "edited after review");

        var check = corpus.Check("Development");

        await check.StartAsync(CancellationToken.None);

        Assert.Contains(corpus.Logger.Messages, message => message.Contains("1 article(s)", StringComparison.Ordinal));
        Assert.Contains(corpus.Logger.Messages, message => message.Contains("Knowledge base check:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_matching_corpus_logs_its_count_and_starts_outside_development()
    {
        using var corpus = new Corpus();
        corpus.Article("connectivity/kb-0001-pairing.md", "kb-0001");
        corpus.Manifest();

        await corpus.Check("Production").StartAsync(CancellationToken.None);

        Assert.Contains(
            corpus.Logger.Messages,
            message => message.Contains("1 article(s)", StringComparison.Ordinal)
                       && message.Contains("1 line(s) in REVIEWED.sha256", StringComparison.Ordinal));
    }

    /// <summary>
    /// Registered by AddRag, so a host cannot forget it: the Web host and the RAG host both get the
    /// check from the same call that gives them the corpus.
    /// </summary>
    [Fact]
    public void AddRag_registers_the_startup_check()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Development" });
        services.AddLogging();
        services.AddRag(configuration);

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                          && descriptor.ImplementationType == typeof(KnowledgeBaseStartupCheck));
    }

    /// <summary>
    /// A corpus directory this test owns: two required articles' worth of structure, a manifest it can
    /// regenerate, and the logger the check writes through.
    /// </summary>
    private sealed class Corpus : IDisposable
    {
        public Corpus()
        {
            Root = Path.Combine(Path.GetTempPath(), "rag-verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public RecordingLogger<KnowledgeBaseStartupCheck> Logger { get; } = new();

        public string Article(string relativePath, string id, string? body = null)
        {
            var file = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            File.WriteAllText(
                file,
                $"""
                ---
                id: {id}
                title: Article {id}
                category: connectivity
                reviewed_by: docs-team
                reviewed_at: 2026-09-29
                product_scope: ["*"]
                ---

                {(body ?? "Pairing fails until the host forgets the device.")}
                """);

            return file;
        }

        /// <summary>Writes a manifest over what is on disk, plus any line the case wants there.</summary>
        public void Manifest(string? extra = null)
        {
            var lines = ArticleFiles()
                .Select(file =>
                    $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()}  "
                    + Path.GetRelativePath(Root, file).Replace('\\', '/'))
                .ToList();

            if (extra is not null)
            {
                lines.Add($"{new string('a', 64)}  {extra}");
            }

            File.WriteAllText(Path.Combine(Root, KnowledgeBaseLoader.ReviewedHashesFileName), string.Join('\n', lines) + '\n');
        }

        public KnowledgeBaseLoadResult Load() =>
            new KnowledgeBaseLoader(new RecordingLogger<KnowledgeBaseLoader>(), Options.Create(new KnowledgeBaseOptions())).Load(Root);

        public KnowledgeBaseStartupCheck Check(string environment) =>
            new(Load(), new HostingEnvironment { EnvironmentName = environment }, Logger);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private IEnumerable<string> ArticleFiles() =>
            Directory.EnumerateFiles(Root, "*.md", SearchOption.AllDirectories)
                .Where(file => !string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.Ordinal);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
