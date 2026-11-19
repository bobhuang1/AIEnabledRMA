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

    /// <summary>What the last recorded measurement was, published so a later run can be compared to it.</summary>
    public GoldenSetBaseline? Baseline { get; init; }

    public required IReadOnlyList<GoldenSetCase> Cases { get; init; }
}

/// <summary>One question, and every article that would be a correct citation for it.</summary>
public sealed record GoldenSetCase
{
    public required string Id { get; init; }

    public required string Question { get; init; }

    public required IReadOnlyList<string> Expected { get; init; }

    /// <summary>Product families the case needs enabled, because the expected article is product-scoped.</summary>
    public IReadOnlyList<string>? Scope { get; init; }

    public string? Note { get; init; }
}

/// <summary>The recorded measurement. Documentation for the next person, not an input to scoring.</summary>
public sealed record GoldenSetBaseline
{
    public string? MeasuredOn { get; init; }

    public string? Index { get; init; }

    public double RecallAt3 { get; init; }

    public double Mrr { get; init; }

    public int Cases { get; init; }

    public IReadOnlyList<string>? Misses { get; init; }

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
}

/// <summary>The whole run: per-case results plus the two numbers the floor is compared against.</summary>
public sealed record GoldenSetScore
{
    public required IReadOnlyList<GoldenSetCaseScore> Cases { get; init; }

    /// <summary>Share of cases whose top three included one of the expected articles.</summary>
    public required double RecallAt3 { get; init; }

    /// <summary>Mean reciprocal rank of the first correct article, counting a miss as zero.</summary>
    public required double Mrr { get; init; }

    public required double Floor { get; init; }

    /// <summary>The recorded MRR bar, or null when the set does not floor it.</summary>
    public required double? MrrFloor { get; init; }

    public int Hits => Cases.Count(c => c.Rank > 0);

    public IEnumerable<GoldenSetCaseScore> Misses => Cases.Where(c => c.Rank == 0);

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

        var hits = scored.Count(c => c.Rank > 0);

        return new GoldenSetScore
        {
            Cases = scored,
            RecallAt3 = hits / (double)scored.Count,
            Mrr = scored.Average(c => c.Rank == 0 ? 0d : 1d / c.Rank),
            Floor = set.RecallAt3Floor,
            MrrFloor = set.MrrFloor,
        };
    }

    /// <summary>The product asks for this many articles, so the eval measures the same window.</summary>
    public const int TopK = 3;

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
