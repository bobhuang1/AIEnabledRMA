using System.Text.Json;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Knowledge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// P5's acceptance test, written before any vector work exists: retrieval must not get worse than
/// the lexical index measured here.
/// </summary>
/// <remarks>
/// A recall number is only as good as the questions behind it, so the cases live in
/// <c>eval/retrieval-golden.json</c> rather than in this file. That file also owns the floor, so a
/// change that moves the bar is a reviewable diff in the data instead of an edit to one constant.
///
/// The questions are aimed at the shipped corpus, which is why this loads the real directory rather
/// than a fixture: a P5 chunker that splits articles differently, or an embedding model that ranks a
/// sibling above the answer, shows up here as a specific question in the failure message.
///
/// Two coverage rules keep the number honest over time. A case may not name an article the corpus
/// has dropped, or it fails for a reason that reads like a regression; and every article needs at
/// least one case, or recall@3 silently measures a shrinking share of the corpus as articles are
/// added.
/// </remarks>
public sealed class RetrievalGoldenSetTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record GoldenCase(
        string Id,
        string Question,
        string[] Expected,
        string[]? Scope = null,
        string? Note = null);

    private sealed record GoldenSet(
        string Description,
        double RecallAt3Floor,
        GoldenCase[] Cases);

    [Fact]
    public void The_lexical_index_meets_the_recorded_recall_at_three()
    {
        var root = RepositoryRoot();
        var goldenPath = Path.Combine(root, "eval", "retrieval-golden.json");

        Assert.True(File.Exists(goldenPath), $"the golden question set is missing at {goldenPath}");

        var golden = JsonSerializer.Deserialize<GoldenSet>(File.ReadAllText(goldenPath), Json);

        Assert.NotNull(golden);
        Assert.NotEmpty(golden.Cases);
        Assert.InRange(golden.RecallAt3Floor, 0d, 1d);

        Assert.Equal(
            golden.Cases.Length,
            golden.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            golden.Cases.Length,
            golden.Cases.Select(c => c.Question).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var loaded = new KnowledgeBaseLoader(
            NullLogger<KnowledgeBaseLoader>.Instance,
            Options.Create(new KnowledgeBaseOptions { RequireReview = true }))
            .Load(Path.Combine(root, "knowledge-base"));

        Assert.Empty(loaded.Errors);

        // The families the golden set names, and only those. A scoped article is inert unless the
        // deployment enables its family, so the eval states the configuration it measures instead
        // of inheriting whatever a default happens to be.
        var families = golden.Cases
            .SelectMany(c => c.Scope ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var index = new KnowledgeIndex(
            loaded,
            Options.Create(new RagRetrievalOptions()),
            Options.Create(new RagOptions { EnabledProductFamilies = families }));

        var corpusIds = loaded.Articles.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = golden.Cases
            .SelectMany(c => c.Expected.Select(id => (Case: c.Id, Article: id)))
            .Where(x => !corpusIds.Contains(x.Article))
            .Select(x => $"{x.Case} expects {x.Article}")
            .ToList();

        Assert.True(
            unknown.Count == 0,
            "the golden set names articles that are not in the corpus, so those cases would fail "
            + "for a reason that looks like a retrieval regression:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unknown));

        var expectedIds = golden.Cases
            .SelectMany(c => c.Expected)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uncovered = loaded.Articles
            .Select(a => a.Id)
            .Where(id => !expectedIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            "no golden question expects these articles, so the eval would measure less and less of "
            + "the corpus as it grows; add a case to eval/retrieval-golden.json for:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, uncovered));

        var results = golden.Cases
            .Select(goldenCase =>
            {
                var retrieval = index.Search(new KnowledgeRetrievalQuery
                {
                    Query = goldenCase.Question,
                    ScopeTokens = (goldenCase.Scope ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    CategoryHints = [],
                    TopK = 3,
                });

                var top3 = retrieval.Articles.Select(a => a.Id).ToArray();
                var rank = top3
                    .Select((id, position) => (Id: id, Position: position))
                    .Where(x => goldenCase.Expected.Contains(x.Id, StringComparer.OrdinalIgnoreCase))
                    .Select(x => x.Position + 1)
                    .DefaultIfEmpty(0)
                    .First();

                output.WriteLine(
                    $"{goldenCase.Id} {(rank > 0 ? "hit " : "MISS")} rank={rank} [{string.Join(",", top3)}] "
                    + $"<- {goldenCase.Question}");

                return (Case: goldenCase, Rank: rank);
            })
            .ToList();

        var hits = results.Count(r => r.Rank > 0);
        var recall = hits / (double)results.Count;
        var meanReciprocalRank = results.Average(r => r.Rank == 0 ? 0d : 1d / r.Rank);

        output.WriteLine(
            $"recall@3 = {recall:0.000} ({hits}/{results.Count}); MRR = {meanReciprocalRank:0.000}; "
            + $"floor = {golden.RecallAt3Floor:0.000}");

        var misses = results
            .Where(r => r.Rank == 0)
            .Select(r => $"{r.Case.Id} '{r.Case.Question}' expected one of [{string.Join(",", r.Case.Expected)}]")
            .ToList();

        Assert.True(
            recall >= golden.RecallAt3Floor,
            $"recall@3 is {recall:0.000} ({hits}/{results.Count}), below the recorded floor "
            + $"{golden.RecallAt3Floor:0.000}. Either retrieval got worse or the floor was "
            + "deliberately lowered; say which in eval/retrieval-golden.json."
            + Environment.NewLine
            + string.Join(Environment.NewLine, misses));
    }

    /// <summary>
    /// Walks up from the test binary to the directory that holds the solution, because the suite can
    /// be started from a shell, an IDE or CI and none of those share a working directory. The RAG
    /// project copies the corpus next to the binary, so stopping at the first directory containing
    /// <c>knowledge-base</c> would read a stale copy.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !(Directory.Exists(Path.Combine(directory.FullName, "knowledge-base"))
                    && File.Exists(Path.Combine(directory.FullName, "AIEnabledRma.slnx"))))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
