using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Triage;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Evaluation;
using AIEnabledRma.Rag.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIEnabledRma.Tests;

/// <summary>
/// P5's acceptance test, written before any vector work exists: retrieval must not get worse than the
/// lexical index measured here.
/// </summary>
/// <remarks>
/// A recall number is only as good as the questions behind it, so the cases live in
/// <c>eval/retrieval-golden.json</c> rather than in this file. That file also owns the floor, so a
/// change that moves the bar is a reviewable diff in the data instead of an edit to one constant.
///
/// Scoring goes through <see cref="IKnowledgeRetriever"/>: this test composes the host's own
/// registration with <c>AddRag</c> and scores whatever that resolves. A keyed vector store becomes the
/// measured implementation by being registered there, with no change to this file, the cases or the
/// floor — which is what makes "not worse than the lexical index" a claim about the product rather
/// than about one class.
///
/// The questions are aimed at the shipped corpus, which is why this loads the real directory rather
/// than a fixture: a P5 chunker that splits articles differently, or an embedding model that ranks a
/// sibling above the answer, shows up here as a specific question in the failure message.
/// </remarks>
public sealed class RetrievalGoldenSetTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_registered_retriever_meets_the_recorded_recall_at_three()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepositoryRoot();
        var goldenPath = Path.Combine(root, "eval", "retrieval-golden.json");

        Assert.True(File.Exists(goldenPath), $"the golden question set is missing at {goldenPath}");

        var golden = RetrievalGoldenSet.Load(goldenPath);

        Assert.NotEmpty(golden.Cases);
        Assert.InRange(golden.RecallAt3Floor, 0d, 1d);

        // Required, not optional: the data file this suite scores has to state both bars, or a
        // change could remove the MRR floor by editing one line and nothing would notice.
        Assert.NotNull(golden.MrrFloor);
        Assert.InRange(golden.MrrFloor!.Value, 0d, 1d);

        Assert.Equal(
            golden.Cases.Count,
            golden.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            golden.Cases.Count,
            golden.Cases.Select(c => c.Question).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The host's own registration, configured the way the eval states it measures: the real corpus
        // path, review on, and the product families the scoped cases name. A vector store replaces
        // IKnowledgeRetriever here and is measured by these same cases.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ConfigurationFor(root, golden))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRag(configuration);

        await using var provider = services.BuildServiceProvider();

        var loaded = provider.GetRequiredService<KnowledgeBaseLoadResult>();

        Assert.Empty(loaded.Errors);

        var corpusIds = loaded.Articles.Select(a => a.Id).ToList();
        var (unknown, uncovered) = RetrievalGoldenSet.Coverage(golden, corpusIds);

        Assert.True(
            unknown.Count == 0,
            "the golden set names articles that are not in the corpus, so those cases would fail "
            + "for a reason that looks like a retrieval regression:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unknown));

        Assert.True(
            uncovered.Count == 0,
            "no golden question expects these articles, so the eval would measure less and less of "
            + "the corpus as it grows; add a case to eval/retrieval-golden.json for:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, uncovered));

        var retriever = provider.GetRequiredService<IKnowledgeRetriever>();
        var score = await RetrievalGoldenSet.ScoreAsync(golden, retriever, ct);

        foreach (var caseScore in score.Cases)
        {
            output.WriteLine(
                $"{caseScore.Case.Id} {(caseScore.Rank > 0 ? "hit " : "MISS")} rank={caseScore.Rank} "
                + $"[{string.Join(",", caseScore.Top)}] <- {caseScore.Case.Question}");
        }

        output.WriteLine(
            $"recall@3 = {score.RecallAt3:0.000} ({score.Hits}/{score.Cases.Count}); "
            + $"MRR = {score.Mrr:0.000}; floors = {score.Floor:0.000} recall, {score.MrrFloor:0.000} MRR");

        var misses = score.Misses
            .Select(c =>
                $"{c.Case.Id} '{c.Case.Question}' expected one of [{string.Join(",", c.Case.Expected)}]")
            .ToList();

        Assert.True(
            score.MeetsFloor,
            $"recall@3 is {score.RecallAt3:0.000} ({score.Hits}/{score.Cases.Count}), below the "
            + $"recorded floor {score.Floor:0.000}. Either retrieval got worse or the floor was "
            + "deliberately lowered; say which in eval/retrieval-golden.json."
            + Environment.NewLine
            + string.Join(Environment.NewLine, misses));

        // Recall holding still while MRR falls is exactly the regression recall@3 cannot see: the
        // same questions still appear in the top three, just further down, which is a worse citation
        // for the model. The dropped cases are named, because "MRR is low" is not a diagnosis.
        var lowered = score.Cases
            .Where(c => c.Rank > 1)
            .Select(c => $"{c.Case.Id} rank={c.Rank} [{string.Join(",", c.Top)}]")
            .ToList();

        Assert.True(
            score.MeetsMrrFloor,
            $"MRR is {score.Mrr:0.000}, below the recorded floor {score.MrrFloor:0.000}. recall@3 can "
            + "hold while hits slip down the list, so this is the number that notices. Cases not at "
            + "rank 1:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, lowered));

        // A floor says "not worse than the bar"; the baseline says "this is exactly what was
        // measured". Without this, the recorded numbers go stale silently: edit a case, add an
        // article, change the tokeniser, and as long as the floor still passes the file keeps
        // claiming a measurement nobody can reproduce. The tolerance is for averaging doubles,
        // not for drift: one case changed is at least 0.025 on recall with forty questions.
        var baseline = golden.Baseline;

        Assert.True(
            baseline is not null,
            "eval/retrieval-golden.json has no baseline block, so the recorded measurement cannot be "
            + "checked against a run. Re-record it in the same commit as the change that moved it.");

        Assert.True(
            Math.Abs(score.RecallAt3 - baseline!.RecallAt3) < 1e-9,
            $"recall@3 measures {score.RecallAt3:0.000} ({score.Hits}/{score.Cases.Count}) but the "
            + $"baseline records {baseline.RecallAt3:0.000}. If the change is intended, re-record "
            + "baseline.recallAt3 — and the floor, if it moved — in eval/retrieval-golden.json as a "
            + "deliberate edit; if not, this is the regression the baseline exists to catch."
            + Environment.NewLine
            + string.Join(Environment.NewLine, misses));

        Assert.True(
            Math.Abs(score.Mrr - baseline.Mrr) < 1e-9,
            $"MRR measures {score.Mrr:0.000} but the baseline records {baseline.Mrr:0.000}. Re-record "
            + "baseline.mrr deliberately if the change is intended; a hit that moved rank without "
            + "changing recall@3 is exactly what this number is for."
            + Environment.NewLine
            + string.Join(Environment.NewLine, lowered));

        Assert.True(
            score.Cases.Count == baseline.Cases,
            $"the set now has {score.Cases.Count} cases but the baseline was measured on "
            + $"{baseline.Cases}. A changed case count moves both numbers by arithmetic alone, so "
            + "re-record baseline.cases, recallAt3 and mrr together deliberately.");
    }

    /// <summary>
    /// The swap path itself, measured the way a deployment would take it: the configuration key
    /// selects the second implementation, and the same forty cases and the same floors score it.
    /// </summary>
    /// <remarks>
    /// The seam test below proves the scorer follows whatever retriever it is handed, but it hands it
    /// a stub. This one resolves <see cref="IKnowledgeRetriever"/> from the host's registration — the
    /// path the triage pipeline takes — so "a vector store becomes the measured implementation by
    /// being registered" is exercised end to end rather than asserted about an interface.
    ///
    /// The baseline is not compared here on purpose: it records the default lexical measurement, and
    /// a second implementation that clears the floors is not claiming to reproduce that number.
    /// </remarks>
    [Fact]
    public async Task The_synonym_retriever_is_measured_by_the_same_cases_and_floors()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepositoryRoot();
        var golden = RetrievalGoldenSet.Load(Path.Combine(root, "eval", "retrieval-golden.json"));

        var settings = ConfigurationFor(root, golden);
        settings["Rag:Retrieval:SynonymExpansion"] = "true";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRag(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        await using var provider = services.BuildServiceProvider();

        var retriever = provider.GetRequiredService<IKnowledgeRetriever>();

        // What is measured has to be the implementation the setting chose, not whichever one the
        // default happens to construct: otherwise this passes while testing the lexical index twice.
        Assert.IsType<SynonymExpandingRetriever>(retriever);

        var score = await RetrievalGoldenSet.ScoreAsync(golden, retriever, ct);

        output.WriteLine(
            $"synonym retriever: recall@3 = {score.RecallAt3:0.000} ({score.Hits}/{score.Cases.Count}); "
            + $"MRR = {score.Mrr:0.000}");

        Assert.True(
            score.MeetsFloor,
            $"the synonym retriever scores recall@3 {score.RecallAt3:0.000} "
            + $"({score.Hits}/{score.Cases.Count}), below the floor {score.Floor:0.000}. A second "
            + "implementation behind the seam is held to the same bar, or the seam is decoration."
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                score.Misses.Select(c => $"{c.Case.Id} '{c.Case.Question}'")));

        Assert.True(score.MeetsMrrFloor, $"the synonym retriever scores MRR {score.Mrr:0.000}, below "
            + $"the floor {score.MrrFloor:0.000}.");
    }

    /// <summary>
    /// Pins the seam rather than the index: the same cases and floor, two different retrievers, and the
    /// score follows the retriever. Without this the eval could pass while measuring nothing but the
    /// class it happens to construct.
    /// </summary>
    [Fact]
    public async Task The_scorer_measures_the_retriever_it_is_given()
    {
        var ct = TestContext.Current.CancellationToken;

        var golden = new GoldenSet
        {
            Description = "in memory, for the seam only",
            RecallAt3Floor = 1d,
            Cases =
            [
                new GoldenSetCase { Id = "s-1", Question = "first", Expected = ["kb-0001"] },
                new GoldenSetCase { Id = "s-2", Question = "second", Expected = ["kb-0002"] },
                new GoldenSetCase
                {
                    Id = "s-3",
                    Question = "scoped",
                    Expected = ["kb-0019"],
                    Scope = ["Nimbus Beacon"],
                },
            ],
        };

        var queries = new List<KnowledgeRetrievalQuery>();

        var partial = new StubRetriever(query =>
        {
            queries.Add(query);

            // Only the scoped question is answered correctly: enough to prove the rank rule, and that
            // the scope each case declares reaches the retriever unchanged.
            return Articles(query.Query == "scoped" ? "kb-0019" : "kb-0007");
        });

        var measured = await RetrievalGoldenSet.ScoreAsync(golden, partial, ct);

        Assert.Equal(1, measured.Hits);
        Assert.Equal(1d / 3, measured.RecallAt3, 3);
        Assert.Equal(1d / 3, measured.Mrr, 3);
        Assert.False(measured.MeetsFloor);
        Assert.All(queries, query => Assert.Equal(RetrievalGoldenSet.TopK, query.TopK));
        Assert.Contains(queries, query => query.ScopeTokens.Contains("Nimbus Beacon"));

        var perfect = new StubRetriever(query =>
            Articles(golden.Cases.Single(c => c.Question == query.Query).Expected[0]));

        var passing = await RetrievalGoldenSet.ScoreAsync(golden, perfect, ct);

        Assert.Equal(1d, passing.RecallAt3);
        Assert.Equal(1d, passing.Mrr);
        Assert.True(passing.MeetsFloor);
    }

    /// <summary>The corpus path, review, and families the eval states it measures with.</summary>
    private static Dictionary<string, string?> ConfigurationFor(string root, GoldenSet golden)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Rag:KnowledgeBasePath"] = Path.Combine(root, "knowledge-base"),

            // On, because the published corpus is reviewed and the eval should not pass on an article
            // the host would refuse to serve.
            ["Rag:KnowledgeBase:RequireReview"] = "true",
        };

        var families = RetrievalGoldenSet.RequiredFamilies(golden);

        for (var index = 0; index < families.Count; index++)
        {
            settings[$"Rag:EnabledProductFamilies:{index}"] = families[index];
        }

        return settings;
    }

    private static IReadOnlyList<RetrievedArticle> Articles(string id) =>
    [
        new RetrievedArticle
        {
            Id = id,
            Title = id,
            Category = "evaluation",
            Score = 1d,
        },
    ];

    private sealed class StubRetriever(
        Func<KnowledgeRetrievalQuery, IReadOnlyList<RetrievedArticle>> respond) : IKnowledgeRetriever
    {
        public Task<KnowledgeRetrievalResult> RetrieveAsync(
            KnowledgeRetrievalQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new KnowledgeRetrievalResult { Articles = respond(query) });
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
