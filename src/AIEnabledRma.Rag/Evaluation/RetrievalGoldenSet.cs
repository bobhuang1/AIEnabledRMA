using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Triage;

namespace AIEnabledRma.Rag.Evaluation;

/// <summary>
/// The golden retrieval question set, as data.
/// </summary>
/// <remarks>
/// The cases and the floor live in <c>eval/retrieval-golden.json</c> rather than in this project or a
/// test, so moving the bar is a reviewable diff in the data. This type only says what the shape is;
/// <see cref="RetrievalGoldenSet.ScoreAsync"/> is what turns a retriever into a number.
/// </remarks>
public sealed record GoldenSet
{
    public required string Description { get; init; }

    public string? Acceptance { get; init; }

    /// <summary>recall@3 a retriever must reach to pass. Raising it is allowed; lowering needs a note.</summary>
    public required double RecallAt3Floor { get; init; }

    /// <summary>
    /// Mean reciprocal rank a retriever must reach to pass, when recorded.
    /// </summary>
    /// <remarks>
    /// recall@3 cannot see a hit slipping from rank 1 to rank 3: the same cases still hit inside the
    /// top three, so the number does not move while the citation handed to the model gets worse.
    /// MRR is the measurement that does move, which is why it is floored rather than only printed.
    /// </remarks>
    public double? MrrFloor { get; init; }

    /// <summary>Rules for moving a floor, kept in the data so they are reviewed with the number.</summary>
    public IReadOnlyList<string>? FloorRules { get; init; }

    /// <summary>
    /// Every measurement taken, oldest first. Append-only: a run adds an entry rather than editing
    /// the last one, so the history says how the number got here and not only where it is.
    /// </summary>
    public required IReadOnlyList<GoldenSetMeasurement> Measurements { get; init; }

    /// <summary>The entry a run has to reproduce: the most recent measurement.</summary>
    public GoldenSetMeasurement? Latest => Measurements.Count == 0 ? null : Measurements[^1];

    public required IReadOnlyList<GoldenSetCase> Cases { get; init; }
}

/// <summary>One question, and every article that would be a correct citation for it.</summary>
public sealed record GoldenSetCase
{
    public required string Id { get; init; }

    public required string Question { get; init; }

    /// <summary>
    /// Every correct article. Empty means the right answer is no citation at all.
    /// </summary>
    /// <remarks>
    /// A question the corpus cannot answer is not a question with a wrong answer: a retriever that
    /// cites an unrelated article for it has made an error that recall cannot see, because recall
    /// only asks whether the expected article appeared — and an empty set can never appear. Those
    /// cases therefore count against precision@3 and the rank-1 rate, and are left out of recall's
    /// denominator.
    /// </remarks>
    public required IReadOnlyList<string> Expected { get; init; }

    /// <summary>Product families the case needs enabled, because the expected article is product-scoped.</summary>
    public IReadOnlyList<string>? Scope { get; init; }

    public string? Note { get; init; }
}

/// <summary>One recorded measurement. Documentation for the next person, not an input to scoring.</summary>
public sealed record GoldenSetMeasurement
{
    /// <summary>The day the run happened, as <c>yyyy-MM-dd</c>; entries are ordered by it.</summary>
    public required string MeasuredOn { get; init; }

    /// <summary>What did the retrieving, described well enough to reproduce the run.</summary>
    public string? Index { get; init; }

    /// <summary>
    /// The corpus the number was measured on: <c>sha256:</c> over every article's path and bytes.
    /// </summary>
    /// <remarks>
    /// Without it a moved number is ambiguous. With it, two runs that disagree while the fingerprint
    /// matches are the retriever's doing, and a changed fingerprint says the corpus moved under the
    /// measurement — which is a different investigation. Entries recorded before the field existed
    /// leave it null rather than inventing a value.
    /// </remarks>
    public string? CorpusFingerprint { get; init; }

    public required double RecallAt3 { get; init; }

    public required double Mrr { get; init; }

    public required int Cases { get; init; }

    /// <summary>Answerable cases the recall and MRR figures were divided by.</summary>
    public int? Answerable { get; init; }

    /// <summary>Mean share of correct citations in the top three, over every case. Null before it was recorded.</summary>
    public double? PrecisionAt3 { get; init; }

    /// <summary>Share of every case whose first citation was correct. Null before it was recorded.</summary>
    public double? Rank1Rate { get; init; }

    public IReadOnlyList<string>? Misses { get; init; }

    /// <summary>
    /// How many articles each unanswerable case was given, when any were.
    /// </summary>
    /// <remarks>
    /// The precision and rank-1 aggregates say a citation happened; this says which question
    /// produced it, which is the only form of the answer a reviewer can act on.
    /// </remarks>
    public IReadOnlyDictionary<string, int>? FalseCitations { get; init; }

    /// <summary>
    /// The rank each case landed at, so a later run can be compared question by question.
    /// </summary>
    /// <remarks>
    /// The aggregates cannot see a swap — one case up a rank and another down one leaves recall and
    /// MRR unchanged — and a single changed case is worth naming, not just counting.
    /// </remarks>
    public IReadOnlyDictionary<string, int>? Ranks { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Where one question landed: the rank of its first correct article, or 0 for a miss.</summary>
public sealed record GoldenSetCaseScore
{
    public required GoldenSetCase Case { get; init; }

    /// <summary>1-based rank of the first expected article in the top three; 0 when none appeared.</summary>
    public required int Rank { get; init; }

    public required IReadOnlyList<string> Top { get; init; }

    /// <summary>Whether the corpus has a correct article for this question.</summary>
    public bool IsAnswerable => Case.Expected.Count > 0;

    /// <summary>How many of the cited articles are correct ones. Always 0 for an unanswerable case.</summary>
    public int Relevant => Top.Count(id => Case.Expected.Contains(id, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The share of the three citations that were correct: 0/3, 1/3, 2/3 or 3/3.
    /// </summary>
    /// <remarks>
    /// For an unanswerable question the correct number of citations is zero, so an empty answer
    /// scores 1 and any citation scores 0. Scoring it as 0/3 either way would make the unanswerable
    /// cases a constant in the average, and a retriever that abstains would be indistinguishable from
    /// one that cites an unrelated article.
    /// </remarks>
    public double PrecisionAt3 => IsAnswerable
        ? Relevant / (double)RetrievalGoldenSet.TopK
        : Top.Count == 0 ? 1d : 0d;

    /// <summary>
    /// Whether the first article cited is a correct one.
    /// </summary>
    /// <remarks>
    /// For an unanswerable question the first citation is correctly absent, so an empty answer
    /// counts and any citation does not.
    /// </remarks>
    public bool Rank1 => IsAnswerable
        ? Top.Count > 0 && Case.Expected.Contains(Top[0], StringComparer.OrdinalIgnoreCase)
        : Top.Count == 0;
}

/// <summary>The whole run: per-case results plus the two numbers the floor is compared against.</summary>
public sealed record GoldenSetScore
{
    public required IReadOnlyList<GoldenSetCaseScore> Cases { get; init; }

    /// <summary>
    /// Share of answerable cases whose top three included one of the expected articles.
    /// </summary>
    /// <remarks>
    /// Unanswerable cases are excluded from the denominator, because there is no article for them
    /// to retrieve: counting them as misses would let a change that cites nothing look like a
    /// regression in recall while improving what the model is told.
    /// </remarks>
    public required double RecallAt3 { get; init; }

    /// <summary>Mean reciprocal rank of the first correct article, over answerable cases, a miss counting zero.</summary>
    public required double Mrr { get; init; }

    /// <summary>
    /// Mean share of the three citations that were correct, over every case.
    /// </summary>
    /// <remarks>
    /// The complement of "how much of what the model reads is noise". Unanswerable cases are in the
    /// denominator: the right answer is no citation, so abstaining scores 1 and citing anything scores
    /// 0. That is a different question from recall, which cannot see a wrong article that displaced a
    /// right one it never had.
    /// </remarks>
    public required double PrecisionAt3 { get; init; }

    /// <summary>
    /// Share of every case whose first cited article is a correct one.
    /// </summary>
    /// <remarks>
    /// The metric for "the model was handed the wrong article first" — recall@3 is unchanged by a
    /// swap of rank 1 and 3, and MRR moves only in proportion. Unanswerable cases are in the
    /// denominator and count as correct only when nothing was cited.
    /// </remarks>
    public required double Rank1Rate { get; init; }

    /// <summary>Cases with a correct article in the corpus; recall and MRR are measured over these.</summary>
    public int Answerable => Cases.Count(c => c.IsAnswerable);

    /// <summary>Cases whose right answer is no citation at all.</summary>
    public int Unanswerable => Cases.Count(c => !c.IsAnswerable);

    /// <summary>
    /// Share of unanswerable cases that cited something anyway.
    /// </summary>
    /// <remarks>
    /// A diagnostic rather than a bar: precision@3 already charges for each such citation, and this
    /// says how often the error happens instead of how much it costs.
    /// </remarks>
    public double FalseCitationRate => Unanswerable == 0
        ? 0d
        : Cases.Count(c => !c.IsAnswerable && c.Top.Count > 0) / (double)Unanswerable;

    public required double Floor { get; init; }

    /// <summary>The recorded MRR bar, or null when the set does not floor it.</summary>
    public required double? MrrFloor { get; init; }

    public int Hits => Cases.Count(c => c.Rank > 0);

    public IEnumerable<GoldenSetCaseScore> Misses => Cases.Where(c => c.IsAnswerable && c.Rank == 0);

    public bool MeetsFloor => RecallAt3 >= Floor;

    /// <summary>
    /// The tolerance is for averaging doubles, not for regressions: with forty cases one hit moving
    /// from rank 1 to rank 3 costs about 0.017, which is far more than this admits.
    /// </summary>
    public bool MeetsMrrFloor => MrrFloor is null || Mrr >= MrrFloor.Value - 1e-9;
}

/// <summary>
/// Loads and scores the golden question set.
/// </summary>
/// <remarks>
/// Scoring goes through <see cref="IKnowledgeRetriever"/> and never through a concrete index, which is
/// the whole point: P5's acceptance test is "not worse than the lexical index measured here", so a
/// vector store has to be measured by the same questions, the same top-three rule, and the same floor.
/// Whatever the host registers as the retriever is what gets measured — swapping the implementation is
/// a registration change, not an edit to the scorer or the test that calls it.
/// </remarks>
public static class RetrievalGoldenSet
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads the set from disk. Throws on a malformed file rather than scoring nothing.</summary>
    public static GoldenSet Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var text = File.ReadAllText(path);

        return JsonSerializer.Deserialize<GoldenSet>(text, Json)
            ?? throw new InvalidOperationException($"The golden set at {path} is empty.");
    }

    /// <summary>
    /// Asks the retriever every question and scores the answers.
    /// </summary>
    /// <remarks>
    /// The query shape is fixed here rather than per case, because it is the shape the triage pipeline
    /// uses: top three, no category hints, and only the scope the case declares. A retriever that
    /// needs a different shape would be measuring something the product does not do.
    /// </remarks>
    public static async Task<GoldenSetScore> ScoreAsync(
        GoldenSet set,
        IKnowledgeRetriever retriever,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(retriever);

        if (set.Cases.Count == 0)
        {
            throw new InvalidOperationException("The golden set has no cases, so it can only measure zero.");
        }

        var scored = new List<GoldenSetCaseScore>(set.Cases.Count);

        foreach (var goldenCase in set.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await retriever.RetrieveAsync(
                new KnowledgeRetrievalQuery
                {
                    Query = goldenCase.Question,
                    ScopeTokens = (goldenCase.Scope ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    CategoryHints = [],
                    TopK = TopK,
                },
                cancellationToken);

            var top = result.Articles
                .Select(article => article.Id)
                .Take(TopK)
                .ToArray();

            var rank = top
                .Select((id, position) => (Id: id, Position: position))
                .Where(x => goldenCase.Expected.Contains(x.Id, StringComparer.OrdinalIgnoreCase))
                .Select(x => x.Position + 1)
                .DefaultIfEmpty(0)
                .First();

            scored.Add(new GoldenSetCaseScore
            {
                Case = goldenCase,
                Rank = rank,
                Top = top,
            });
        }

        var answerable = scored.Where(c => c.IsAnswerable).ToList();

        if (answerable.Count == 0)
        {
            throw new InvalidOperationException(
                "The golden set has no answerable cases, so recall and MRR would measure nothing.");
        }

        var hits = answerable.Count(c => c.Rank > 0);

        return new GoldenSetScore
        {
            Cases = scored,
            RecallAt3 = hits / (double)answerable.Count,
            Mrr = answerable.Average(c => c.Rank == 0 ? 0d : 1d / c.Rank),
            PrecisionAt3 = scored.Average(c => c.PrecisionAt3),
            Rank1Rate = scored.Count(c => c.Rank1) / (double)scored.Count,
            Floor = set.RecallAt3Floor,
            MrrFloor = set.MrrFloor,
        };
    }

    /// <summary>The product asks for this many articles, so the eval measures the same window.</summary>
    public const int TopK = 3;

    /// <summary>
    /// A stable fingerprint of the corpus a measurement was taken on.
    /// </summary>
    /// <remarks>
    /// Sorted relative paths plus file bytes, hashed: a renamed, added, removed or edited article
    /// changes it, and file enumeration order cannot. It deliberately covers the files rather than the
    /// parsed articles, so review state and parser changes are visibly not part of the corpus.
    /// </remarks>
    public static string CorpusFingerprint(string knowledgeBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseDirectory);

        var articles = Directory
            .EnumerateFiles(knowledgeBaseDirectory, "*.md", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(knowledgeBaseDirectory, path).Replace('\\', '/')))
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .ToList();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var article in articles)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(article.Relative));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(article.Path));
            hash.AppendData([0]);
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Articles the set names that the corpus does not have, or articles no case names. Both make the
    /// number mean less than it appears to: the first fails for a reason that reads like a regression,
    /// and the second lets recall@3 measure a shrinking share of the corpus as articles are added.
    /// </summary>
    public static (IReadOnlyList<string> Unknown, IReadOnlyList<string> Uncovered) Coverage(
        GoldenSet set,
        IEnumerable<string> corpusArticleIds)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(corpusArticleIds);

        var corpus = corpusArticleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = set.Cases
            .SelectMany(c => c.Expected.Select(id => (Case: c.Id, Article: id)))
            .Where(x => !corpus.Contains(x.Article))
            .Select(x => $"{x.Case} expects {x.Article}")
            .ToList();

        var expected = set.Cases
            .SelectMany(c => c.Expected)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uncovered = corpus
            .Where(id => !expected.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return (unknown, uncovered);
    }

    /// <summary>Product families the cases need enabled, because some expected article is scoped to one.</summary>
    public static IReadOnlyList<string> RequiredFamilies(GoldenSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        return set.Cases
            .SelectMany(c => c.Scope ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
