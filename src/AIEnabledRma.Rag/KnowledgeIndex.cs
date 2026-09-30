using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Triage;
using AIEnabledRma.Rag.Knowledge;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Rag;

/// <summary>
/// In-memory knowledge index. Loaded once at startup and swapped atomically on reload.
///
/// Ranking is deliberately transparent and inspectable: BM25-style lexical scoring over the
/// article token sets, with a scope filter applied *before* scoring. There is no embedding
/// model in the loop, which means retrieval behaves identically offline, in CI, and in
/// production, and an operator can reproduce any result by hand from the article text.
/// </summary>
public sealed class KnowledgeIndex
{
    private readonly KnowledgeBaseLoadResult _loaded;
    private readonly Dictionary<string, KnowledgeArticle> _byId;
    private readonly RagRetrievalOptions _options;
    private readonly HashSet<string> _enabledFamilies;

    public KnowledgeIndex(
        KnowledgeBaseLoadResult loaded,
        IOptions<RagRetrievalOptions> options,
        IOptions<RagOptions> ragOptions)
    {
        _loaded = loaded;
        _options = options.Value;
        _enabledFamilies = ragOptions.Value.EnabledProductFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _byId = loaded.Articles.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<KnowledgeArticle> Articles => _loaded.Articles;

    public IReadOnlyList<string> LoadErrors => _loaded.Errors;

    public DateTimeOffset LoadedAtUtc => _loaded.LoadedAtUtc;

    public string RootPath => _loaded.RootPath;

    public KnowledgeRetrievalResult Search(KnowledgeRetrievalQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Scope filter first. An article outside the session's product scope is not a
        // lower-ranked candidate, it is not a candidate at all.
        var candidates = _loaded.Articles
            .Where(a => IsInScope(a, query.ScopeTokens))
            .ToList();

        if (candidates.Count == 0)
        {
            return new KnowledgeRetrievalResult
            {
                Articles = [],
                CorpusOutOfScope = true,
            };
        }

        var queryTokens = Tokenize(query.Query);
        var categoryHints = query.CategoryHints
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scored = candidates
            .Select(article => new
            {
                Article = article,
                Score = Score(article, queryTokens, categoryHints),
            })
            .Where(x => x.Score > 0d)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Article.Id, StringComparer.Ordinal)
            .ToList();

        if (scored.Count == 0)
        {
            return new KnowledgeRetrievalResult { Articles = [] };
        }

        // Reserve part of the top-K budget for curated siblings before spending it on direct
        // matches. Taking the full top-K here first left the expansion with no room, because
        // it stops as soon as the list is full, so related-article expansion could never run
        // and a short question could never reach the article its hit pointed at. Leftover
        // slots are then backfilled with the next-best direct matches, so reserving space
        // costs precision only when there really are no further matches.
        var reserved = Math.Clamp(_options.MaxRelatedExpansions, 0, Math.Max(0, query.TopK - 1));
        var directSlots = Math.Max(1, query.TopK - reserved);

        var selected = ExpandWithRelated(
            scored.Take(directSlots).Select(x => x.Article).ToList(),
            query.TopK,
            query.ScopeTokens);

        // Backfill any slot the expansion did not use.
        for (var i = directSlots; i < scored.Count && selected.Count < query.TopK; i++)
        {
            if (!selected.Any(a => string.Equals(a.Id, scored[i].Article.Id, StringComparison.OrdinalIgnoreCase)))
            {
                selected.Add(scored[i].Article);
            }
        }

        return new KnowledgeRetrievalResult
        {
            Articles = selected
                .Select((article, index) => new RetrievedArticle
                {
                    Id = article.Id,
                    Title = article.Title,
                    Category = article.Category,
                    // Rank decay rather than the raw score, so a related-article expansion
                    // sorts below the article that pulled it in.
                    Score = Math.Round(
                        (1d / (1d + index)) * Math.Max(scored[0].Score, _options.RelatedArticleFloor),
                        4),
                    Snippet = BuildSnippet(article, queryTokens),
                })
                .ToList(),
        };
    }

    private bool IsInScope(KnowledgeArticle article, IReadOnlySet<string> scopeTokens)
    {
        if (article.IsUniversal)
        {
            return true;
        }

        if (scopeTokens.Count == 0)
        {
            // No scope information for the session: only universal articles are safe. This
            // is the fail-closed direction, and it is what stops a session with an
            // unidentified product from receiving model-specific guidance.
            return false;
        }

        // A scoped article is inert until the deployment names the product family it applies
        // to. `EnabledProductFamilies` is empty by default, so writing a scoped article is
        // not by itself enough to activate product-specific guidance.
        if (_enabledFamilies.Count == 0)
        {
            return false;
        }

        return article.ProductScope.Any(entry =>
            // Both sides must clear their own gate: the article is written for a family the
            // deployment has enabled, and that family appears in the session's scope.
            _enabledFamilies.Contains(entry)
            && (scopeTokens.Contains(entry)
                || scopeTokens.Any(token =>
                    token.Contains(entry, StringComparison.OrdinalIgnoreCase)
                    || entry.Contains(token, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// BM25 over the article's token set. Document frequency is computed across the whole
    /// in-scope corpus, so a term that appears in every article contributes almost nothing
    /// and a term that appears in one article dominates — which is the behaviour you want
    /// for a small, hand-written knowledge base.
    /// </summary>
    private double Score(
        KnowledgeArticle article,
        IReadOnlyList<string> queryTokens,
        HashSet<string> categoryHints)
    {
        if (queryTokens.Count == 0)
        {
            return 0d;
        }

        const double k1 = 1.5;
        const double b = 0.75;

        var totalArticles = Math.Max(1, _loaded.Articles.Count);
        var articleLength = Math.Max(1, article.Tokens.Count);
        var averageLength = Math.Max(
            1d,
            _loaded.Articles.Average(a => Math.Max(1, a.Tokens.Count)));

        double score = 0d;
        var matched = 0;

        foreach (var token in queryTokens.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!article.Tokens.Contains(token))
            {
                continue;
            }

            matched++;

            var documentFrequency = _loaded.Articles.Count(a => a.Tokens.Contains(token));
            var idf = Math.Log(1d + ((totalArticles - documentFrequency + 0.5d) / (documentFrequency + 0.5d)));

            var termFrequency = 1d;
            var normalization = k1 * (1d - b + b * (articleLength / averageLength));

            score += (idf * ((termFrequency * (k1 + 1d)) / (termFrequency + normalization)))
                     * _options.TermFrequencyWeight;
        }

        if (matched == 0)
        {
            return 0d;
        }

        // Coverage bonus: an article matching half the query is more likely to be the right
        // one than an article matching a single high-idf term in isolation.
        var coverage = (double)matched / queryTokens.Count;

        // Category hint bonus, applied only when the query is genuinely about the product
        // family, so a generic question is not pulled toward one category.
        var categoryBonus = categoryHints.Count > 0
                            && categoryHints.Contains(article.Category.ToLowerInvariant())
            ? _options.CategoryHintBoost
            : 0d;

        return (score * _options.CoverageWeight * coverage) + categoryBonus;
    }

    /// <summary>
    /// Adds siblings named in <c>related</c> for the top hit, at a reduced rank. This is what
    /// makes a two-word customer question still reach the step-by-step article.
    /// </summary>
    private List<KnowledgeArticle> ExpandWithRelated(
        List<KnowledgeArticle> seed,
        int topK,
        IReadOnlySet<string> scopeTokens)
    {
        var result = new List<KnowledgeArticle>(seed);

        foreach (var article in seed.Take(_options.MaxRelatedExpansions))
        {
            foreach (var relatedId in article.Related)
            {
                if (result.Count >= topK)
                {
                    return result;
                }

                if (result.Any(a => string.Equals(a.Id, relatedId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!_byId.TryGetValue(relatedId, out var related))
                {
                    continue;
                }

                // Re-apply the scope gate to expanded articles. The candidate list was
                // filtered before scoring, but a `related` link is just a string in front
                // matter: following it without re-checking would let a session scoped to one
                // product family be handed an article scoped to another, which is exactly
                // the leak the scope gate exists to prevent.
                if (!IsInScope(related, scopeTokens))
                {
                    continue;
                }

                result.Add(related);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the passage handed to the model. Limited to a sentence budget so the prompt
    /// stays bounded no matter how long an article is.
    /// </summary>
    private static string BuildSnippet(KnowledgeArticle article, IReadOnlyList<string> queryTokens)
    {
        var querySet = queryTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var paragraphs = article.Body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var ranked = paragraphs
            .Select(p => new
            {
                Paragraph = p,
                Hits = querySet.Count(t => p.Contains(t, StringComparison.OrdinalIgnoreCase)),
            })
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.Paragraph.Length)
            .ToList();

        var builder = new System.Text.StringBuilder();
        foreach (var paragraph in ranked)
        {
            if (builder.Length >= RagRetrievalOptions.MaxSnippetCharacters)
            {
                break;
            }

            // Headings carry the structure of a checklist; keep them even with no token hits.
            if (paragraph.Hits == 0 && !paragraph.Paragraph.StartsWith('#'))
            {
                continue;
            }

            builder.AppendLine(paragraph.Paragraph);
        }

        var snippet = builder.ToString().Trim();
        return snippet.Length >= RagRetrievalOptions.MaxSnippetCharacters
            ? string.Concat(
                snippet.AsSpan(0, RagRetrievalOptions.MaxSnippetCharacters - 1),
                "…")
            : snippet;
    }

    private static IReadOnlyList<string> Tokenize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : System.Text.RegularExpressions.Regex
                .Matches(value.ToLowerInvariant(), "[a-z0-9]+")
                .Select(m => m.Value)
                .Where(t => t.Length > 2)
                .ToArray();
}

public sealed class RagRetrievalOptions
{
    public const string SectionName = "Rag:Retrieval";

    /// <summary>
    /// Hard cap on the characters handed to the model for a single article, so prompt size
    /// cannot grow with the length of an article someone edits into the corpus.
    /// </summary>
    public const int MaxSnippetCharacters = 2400;

    /// <summary>
    /// Weight applied to the raw BM25 term-frequency term. Raising it sharpens the preference
    /// for an article matching a distinctive term; lowering it favours broad coverage.
    /// </summary>
    public double TermFrequencyWeight { get; set; } = 1.0;

    /// <summary>Weight applied to the query-coverage ratio.</summary>
    public double CoverageWeight { get; set; } = 1.0;

    /// <summary>
    /// Score added to an article whose category matches a product-family hint. Deliberately
    /// modest, so it breaks ties rather than overriding lexical relevance.
    /// </summary>
    public double CategoryHintBoost { get; set; } = 0.25;

    /// <summary>How many top articles may pull in their <c>related</c> siblings.</summary>
    public int MaxRelatedExpansions { get; set; } = 2;

    /// <summary>
    /// Floor applied to the reported score of a related-article expansion, so an article with
    /// no lexical overlap of its own still scores above the scope guard's rejection bar.
    /// </summary>
    public double RelatedArticleFloor { get; set; } = 0.05;
}
