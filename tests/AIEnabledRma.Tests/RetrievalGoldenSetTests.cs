using System.Globalization;
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
                caseScore.IsAnswerable
                    ? $"{caseScore.Case.Id} {(caseScore.Rank > 0 ? "hit " : "MISS")} rank={caseScore.Rank} "
                      + $"precision={caseScore.PrecisionAt3:0.00} [{string.Join(",", caseScore.Top)}] "
                      + $"<- {caseScore.Case.Question}"
                    : $"{caseScore.Case.Id} no-answer cited={caseScore.Top.Count} "
                      + $"[{string.Join(",", caseScore.Top)}] <- {caseScore.Case.Question}");
        }

        output.WriteLine(
            $"recall@3 = {score.RecallAt3:0.000} ({score.Hits}/{score.Answerable} answerable); "
            + $"MRR = {score.Mrr:0.000}; precision@3 = {score.PrecisionAt3:0.000}; "
            + $"rank-1 = {score.Rank1Rate:0.000}; unanswerable = {score.Unanswerable} "
            + $"(false citations {score.FalseCitationRate:0.000}); floors = {score.Floor:0.000} recall, "
            + $"{score.MrrFloor:0.000} MRR");

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
        Assert.True(
            golden.Measurements.Count > 0,
            "eval/retrieval-golden.json has no measurements, so the recorded run cannot be checked "
            + "against one. A run appends its entry; it does not start the history over.");

        // Append-only is an order, not a promise: a new run adds its entry to the end, so the dates
        // have to increase (two runs on one day may share a date). History that was edited or
        // reordered reads as plausible and matches no run.
        var days = golden.Measurements
            .Select(m => DateOnly.ParseExact(m.MeasuredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(
            days.Zip(days.Skip(1)).All(pair => pair.First <= pair.Second),
            "measurements are not in ascending measuredOn order. The list is append-only: a new run "
            + "adds an entry at the end, and an existing entry is never edited or reordered.");

        var baseline = golden.Latest!;

        // The two citation metrics are recorded, not only printed: a run that improves recall by
        // citing one article more confidently and one more wrongly has to move them somewhere a
        // reviewer can see, and a later edit cannot delete the fields without failing here.
        Assert.True(
            baseline.PrecisionAt3 is not null && baseline.Rank1Rate is not null,
            $"the latest measurement ({baseline.MeasuredOn}) records no precisionAt3/rank1Rate, so a "
            + "wrong first citation costs nothing on record. Append an entry carrying both.");

        Assert.True(
            Math.Abs(score.PrecisionAt3 - baseline.PrecisionAt3!.Value) < 1e-9,
            $"precision@3 measures {score.PrecisionAt3:0.000} but the latest measurement records "
            + $"{baseline.PrecisionAt3:0.000}. Precision is the share of the three citations that were "
            + "correct, so a change that cites more wrong articles for the same recall lowers it.");

        Assert.True(
            Math.Abs(score.Rank1Rate - baseline.Rank1Rate!.Value) < 1e-9,
            $"the rank-1 rate measures {score.Rank1Rate:0.000} but the latest measurement records "
            + $"{baseline.Rank1Rate:0.000}. This is the share of questions whose first citation was "
            + "correct; a swap of rank 1 and rank 3 leaves recall@3 untouched and moves this.");

        Assert.True(
            score.Unanswerable > 0,
            "the golden set has no unanswerable cases, so precision and the rank-1 rate are only "
            + "measuring answerable questions and cannot notice a confident wrong citation.");

        // The rules for moving the bar live in the data, so changing one is a reviewable diff beside
        // the number it governs. The one rule that matters most is asserted rather than trusted to
        // survive: a floor lowered in the same commit as a scoring change hides the regression the
        // floor exists to catch.
        Assert.True(
            golden.FloorRules is { Count: > 0 },
            "eval/retrieval-golden.json states no floorRules, so how a floor may move is convention "
            + "rather than policy. Restore the rules; they are what a reviewer checks the commit against.");

        Assert.Contains(
            golden.FloorRules!,
            rule => rule.Contains("never lowered in the same commit", StringComparison.Ordinal));

        Assert.Contains(
            golden.FloorRules!,
            rule => rule.Contains("append-only", StringComparison.OrdinalIgnoreCase));

        // The corpus the number belongs to. Two runs that disagree with the same fingerprint are the "
        // retriever's doing; a changed fingerprint means the corpus moved under the measurement, and that "
        // is a different investigation. This cannot run before the corpus loads, so it reads the
        // directory the configuration above pointed AddRag at.
        var corpusFingerprint = RetrievalGoldenSet.CorpusFingerprint(Path.Combine(root, "knowledge-base"));

        Assert.True(
            baseline.CorpusFingerprint is not null,
            $"the latest measurement ({baseline.MeasuredOn}) records no corpusFingerprint, so a number "
            + "that moved cannot be attributed to the corpus or to the retriever. Append an entry that "
            + $"records it: {corpusFingerprint}.");

        Assert.True(
            string.Equals(baseline.CorpusFingerprint, corpusFingerprint, StringComparison.Ordinal),
            $"the corpus is {corpusFingerprint} but the latest measurement was taken on "
            + $"{baseline.CorpusFingerprint} ({baseline.MeasuredOn}). The corpus changed since that run, "
            + "so a difference here is the articles' doing rather than the retriever's: append a new "
            + "measurement taken on this corpus instead of editing the old one.");

        Assert.True(
            Math.Abs(score.RecallAt3 - baseline!.RecallAt3) < 1e-9,
            $"recall@3 measures {score.RecallAt3:0.000} ({score.Hits}/{score.Cases.Count}) but the "
            + $"the latest measurement records {baseline.RecallAt3:0.000}. If the change is intended, "
            + "append a new measurement entry (recallAt3, mrr, ranks, corpusFingerprint — and the floor, "
            + "in its own commit) in eval/retrieval-golden.json; if not, this is the regression the "
            + "history exists to catch."
            + Environment.NewLine
            + string.Join(Environment.NewLine, misses));

        Assert.True(
            Math.Abs(score.Mrr - baseline.Mrr) < 1e-9,
            $"MRR measures {score.Mrr:0.000} but the latest measurement records {baseline.Mrr:0.000}. "
            + "Append a new entry deliberately if the change is intended; a hit that moved rank without "
            + "changing recall@3 is exactly what this number is for."
            + Environment.NewLine
            + string.Join(Environment.NewLine, lowered));

        Assert.True(
            score.Cases.Count == baseline.Cases,
            $"the set now has {score.Cases.Count} cases but the latest measurement was taken on "
            + $"{baseline.Cases}. A changed case count moves both numbers by arithmetic alone, so "
            + "append an entry that records the count, recallAt3 and mrr together.");

        // Per case, not only in aggregate: a rank swap leaves recall and MRR untouched, and "which
        // question moved" is the only useful form of the answer. The delta is published for every
        // case, equal or not, so a run says what it did rather than only that it was allowed.
        var recordedRanks = baseline.Ranks;

        Assert.True(
            recordedRanks is not null,
            "the latest measurement has no ranks, so a run cannot be compared question by question. "
            + "Append an entry with a rank for every case.");

        var drifted = new List<string>();

        foreach (var caseScore in score.Cases)
        {
            // Unanswerable questions are compared by how many articles were cited, not by rank:
            // their rank is 0 by construction, and what can regress is a citation appearing where
            // there should be none.
            if (!caseScore.IsAnswerable)
            {
                var recordedCitations = baseline.FalseCitations?.GetValueOrDefault(caseScore.Case.Id) ?? 0;

                output.WriteLine(
                    $"{caseScore.Case.Id} no-answer cited {recordedCitations} -> {caseScore.Top.Count}"
                    + (recordedCitations == caseScore.Top.Count ? string.Empty : "  CHANGED"));

                if (recordedCitations != caseScore.Top.Count)
                {
                    drifted.Add(
                        $"{caseScore.Case.Id} cited {recordedCitations} -> {caseScore.Top.Count} "
                        + "(no article answers it)");
                }

                continue;
            }

            if (!recordedRanks!.TryGetValue(caseScore.Case.Id, out var recorded))
            {
                drifted.Add($"{caseScore.Case.Id} (not in the latest entry) -> rank {caseScore.Rank}");
                continue;
            }

            output.WriteLine(
                $"{caseScore.Case.Id} rank {recorded} -> {caseScore.Rank}"
                + (recorded == caseScore.Rank ? string.Empty : "  CHANGED"));

            if (recorded != caseScore.Rank)
            {
                drifted.Add($"{caseScore.Case.Id} {recorded} -> {caseScore.Rank}");
            }
        }

        Assert.True(
            drifted.Count == 0,
            $"{drifted.Count} case(s) moved against the latest entry (ranks for answerable "
            + "questions, citation counts for unanswerable ones): "
            + string.Join(", ", drifted)
            + ". If the change is intended, append a new measurement (ranks, recallAt3, mrr and the "
            + "corpusFingerprint; the floor separately) deliberately; if not, this is the per-case "
            + "regression the aggregate could hide.");
    }

    /// <summary>
    /// The ablation behind the synonym map: the registered retriever is the expanding one and clears
    /// the floor, and turning the map off measures exactly what the lexical index scored before it
    /// existed. The difference between the two is the map's contribution, measured.
    /// </summary>
    /// <remarks>
    /// The acceptance test above compares the default to the latest recorded measurement; this one
    /// answers the question that comparison cannot: is the improvement the map, or the default having
    /// changed? The lexical numbers are pinned here — 0.850 recall, 0.750 MRR — so a change that makes
    /// the map irrelevant, or makes the fallback worse, fails with the pair in the message.
    ///
    /// The fallback is expected to be below the 0.9 floor: that is the point of raising it on the
    /// measurement that includes the map, not a regression in the ablation.
    /// </remarks>
    [Fact]
    public async Task Disabling_the_synonym_map_measures_the_lexical_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepositoryRoot();
        var golden = RetrievalGoldenSet.Load(Path.Combine(root, "eval", "retrieval-golden.json"));

        var settings = ConfigurationFor(root, golden);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRag(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        await using var provider = services.BuildServiceProvider();

        var retriever = provider.GetRequiredService<IKnowledgeRetriever>();

        // The default is the expanding retriever; a configuration that silently resolved the plain
        // index would make the numbers below look right for the wrong reason.
        Assert.IsType<SynonymExpandingRetriever>(retriever);

        var score = await RetrievalGoldenSet.ScoreAsync(golden, retriever, ct);

        output.WriteLine(
            $"with the map: recall@3 = {score.RecallAt3:0.000} ({score.Hits}/{score.Answerable}); "
            + $"MRR = {score.Mrr:0.000}");

        Assert.True(
            score.MeetsFloor,
            $"the default retriever scores recall@3 {score.RecallAt3:0.000} "
            + $"({score.Hits}/{score.Answerable}), below the floor {score.Floor:0.000}."
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                score.Misses.Select(c => $"{c.Case.Id} '{c.Case.Question}'")));

        var lexicalSettings = ConfigurationFor(root, golden);
        lexicalSettings["Rag:Retrieval:SynonymExpansion"] = "false";

        var lexicalServices = new ServiceCollection();
        lexicalServices.AddLogging();
        lexicalServices.AddRag(new ConfigurationBuilder().AddInMemoryCollection(lexicalSettings).Build());

        await using var lexicalProvider = lexicalServices.BuildServiceProvider();

        var lexicalRetriever = lexicalProvider.GetRequiredService<IKnowledgeRetriever>();

        Assert.IsType<KnowledgeIndexRetriever>(lexicalRetriever);

        var lexical = await RetrievalGoldenSet.ScoreAsync(golden, lexicalRetriever, ct);

        output.WriteLine(
            $"without the map: recall@3 = {lexical.RecallAt3:0.000} ({lexical.Hits}/{lexical.Answerable}); "
            + $"MRR = {lexical.Mrr:0.000}");

        Assert.True(
            Math.Abs(lexical.RecallAt3 - 0.850) < 1e-9,
            $"the lexical index measures recall@3 {lexical.RecallAt3:0.000} where the recorded ablation "
            + "is 0.850. Either the fallback changed or the map is no longer what separates the two.");

        Assert.True(
            Math.Abs(lexical.Mrr - 0.750) < 1e-9,
            $"the lexical index measures MRR {lexical.Mrr:0.000} where the recorded ablation is 0.750.");
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
            Measurements = [],
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

    /// <summary>
    /// An unanswerable question is scored on abstaining, not on which article it happened to cite.
    /// </summary>
    /// <remarks>
    /// The two stubs differ in one response only: the second returns nothing for the unanswerable
    /// question. Recall is identical because it is divided by the answerable questions alone; the
    /// citation metrics are what separate the two behaviour. Without this the unanswerable cases
    /// would be a constant in the averages and could not tell abstention from a confident wrong
    /// citation.
    /// </remarks>
    [Fact]
    public async Task An_unanswerable_question_is_scored_on_abstaining()
    {
        var ct = TestContext.Current.CancellationToken;

        var golden = new GoldenSet
        {
            Description = "in memory, one answerable and one not",
            RecallAt3Floor = 1d,
            Measurements = [],
            Cases =
            [
                new GoldenSetCase { Id = "a-1", Question = "answerable", Expected = ["kb-0001"] },
                new GoldenSetCase { Id = "u-1", Question = "unanswerable", Expected = [] },
            ],
        };

        var citing = new StubRetriever(query => Articles("kb-0001"));
        var measured = await RetrievalGoldenSet.ScoreAsync(golden, citing, ct);

        Assert.Equal(1d, measured.RecallAt3);
        Assert.Equal(1, measured.Answerable);
        Assert.Equal(1, measured.Unanswerable);
        Assert.Equal(1d / 6, measured.PrecisionAt3, 3);
        Assert.Equal(0.5d, measured.Rank1Rate, 3);
        Assert.Equal(1d, measured.FalseCitationRate);

        var abstaining = new StubRetriever(query =>
            query.Query == "unanswerable" ? [] : Articles("kb-0001"));
        var abstained = await RetrievalGoldenSet.ScoreAsync(golden, abstaining, ct);

        // Same recall, better citations: this is the difference the new cases exist to measure.
        Assert.Equal(1d, abstained.RecallAt3);
        Assert.Equal(2d / 3, abstained.PrecisionAt3, 3);
        Assert.Equal(1d, abstained.Rank1Rate);
        Assert.Equal(0d, abstained.FalseCitationRate);
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
