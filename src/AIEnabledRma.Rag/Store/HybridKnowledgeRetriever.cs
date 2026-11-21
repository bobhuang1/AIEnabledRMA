using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Triage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Rag.Store;

/// <summary>Turns the vector store on (<c>Rag:VectorStore</c>).</summary>
public sealed class VectorStoreOptions
{
    public const string SectionName = "Rag:VectorStore";

    /// <summary>
    /// When true, retrieval runs hybrid (full-text + vector) over the pgvector store filled by
    /// <c>DbAdmin kb-ingest</c>. When false (default) the in-memory lexical index serves, which is
    /// what the offline demo and most unit tests use.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Chunks fetched before grouping into documents.</summary>
    public int ChunkCandidates { get; set; } = 20;

    /// <summary>Most chunks of one document joined into its snippet.</summary>
    public int ChunksPerDocument { get; set; } = 2;

    /// <summary>
    /// Weight of a lexical rank against one store list when a redacted case competes with articles
    /// (a case at the top of both store lists scores 2/61; the top article scores LexicalWeight/61).
    /// </summary>
    public double LexicalWeight { get; set; } = 1;
}

/// <summary>
/// Hybrid retrieval over <see cref="PgVectorKnowledgeStore"/>, returning documents (articles and
/// redacted cases) with their best chunks as the snippet. The product-scope rule is the lexical
/// index's rule, resolved here to the list of scope entries a session may see and applied in SQL
/// before ranking. If the store cannot be reached, retrieval falls back to the in-memory index of
/// reviewed articles rather than answering with nothing.
/// </summary>
public sealed class HybridKnowledgeRetriever(
    PgVectorKnowledgeStore store,
    IKnowledgeRetriever fallback,
    IOptions<VectorStoreOptions> options,
    IOptions<RagOptions> ragOptions,
    ILogger<HybridKnowledgeRetriever> logger) : IKnowledgeRetriever
{
    /// <summary>Scales an article only the store found, so it ranks below every lexical article.</summary>
    private const double StoreOnlyArticleWeight = 0.25;

    public async Task<KnowledgeRetrievalResult> RetrieveAsync(KnowledgeRetrievalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var allowed = AllowedScopes(query.ScopeTokens, ragOptions.Value.EnabledProductFamilies);

        IReadOnlyList<KbChunkHit> hits;
        try
        {
            hits = await store.SearchAsync(query.Query, allowed, options.Value.ChunkCandidates, cancellationToken);
        }
        catch (EmbeddingModelMismatchException ex)
        {
            // Configuration error, not an outage: the store was built with another model.
            logger.LogError(ex, "Embedding model mismatch; serving from the in-memory article index until the store is re-embedded.");
            return await fallback.RetrieveAsync(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Vector store retrieval failed; serving from the in-memory article index.");
            return await fallback.RetrieveAsync(query, cancellationToken);
        }

        // The in-memory lexical retriever (synonym expansion, category boost, curated related
        // articles) is the third ranked list. Its ranking of reviewed articles is the measured
        // baseline, so fusing it in means the store adds recall (redacted cases, chunk-level
        // matches) without demoting what the lexical index already gets right.
        var lexical = await fallback.RetrieveAsync(
            query with { TopK = Math.Max(query.TopK * 3, 10) },
            cancellationToken);

        if (hits.Count == 0 && lexical.Articles.Count == 0)
        {
            return new KnowledgeRetrievalResult
            {
                Articles = [],
                CorpusOutOfScope = lexical.CorpusOutOfScope && await store.CountInScopeAsync(allowed, cancellationToken) == 0,
            };
        }

        var hints = query.CategoryHints.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fused = new Dictionary<(string Kind, string Id), (RetrievedArticle Article, double Score)>();
        var storeDocuments = new Dictionary<(string Kind, string Id), (RetrievedArticle Article, double Score)>();

        foreach (var group in hits.GroupBy(h => (h.Kind, h.SourceId)))
        {
            var best = group.OrderByDescending(h => h.Score).Take(Math.Max(1, options.Value.ChunksPerDocument)).ToList();

            // A small nudge for the device's own category, the same idea as the lexical index's
            // category boost: enough to break ties, not to outrank a better match.
            var boost = hints.Contains(best[0].Category) ? 0.25d / (PgVectorKnowledgeStore.RrfK + 1) : 0d;
            storeDocuments[group.Key] = (new RetrievedArticle
            {
                Id = best[0].SourceId,
                Title = best[0].Title,
                Category = best[0].Category,
                Score = 0,
                Snippet = string.Join("\n\n", best.OrderBy(h => h.Ordinal).Select(h => h.HeadingPath + "\n" + h.Text)),
                SourceKind = best[0].Kind,
                ChunkRef = string.Join(",", best.OrderBy(h => h.Ordinal).Select(h => $"{h.SourceId}#{h.Ordinal}")),
                CitationLabel = best[0].CitationLabel,
            }, best[0].Score + boost);
        }

        // Articles keep the lexical ranking: it is reviewed, tuned and measured on the golden set,
        // and the store must not demote what it gets right. Where the store matched the same
        // article, its best chunks replace the whole-article snippet.
        for (var rank = 0; rank < lexical.Articles.Count; rank++)
        {
            var article = lexical.Articles[rank];
            var key = (article.SourceKind, article.Id);
            var score = options.Value.LexicalWeight / (PgVectorKnowledgeStore.RrfK + rank + 1);
            fused[key] = storeDocuments.TryGetValue(key, out var chunked)
                ? (article with { Snippet = chunked.Article.Snippet, ChunkRef = chunked.Article.ChunkRef }, score)
                : (article, score);
        }

        // The store adds what the lexical index cannot see. Redacted CRM cases compete on their own
        // fused score; an article only the store found is appended after the lexical ones.
        foreach (var (key, document) in storeDocuments)
        {
            if (fused.ContainsKey(key))
            {
                continue;
            }

            fused[key] = key.Kind == KbKinds.Article
                ? (document.Article, document.Score * StoreOnlyArticleWeight)
                : document;
        }

        return new KnowledgeRetrievalResult
        {
            Articles = fused.Values
                .OrderByDescending(d => d.Score)
                .ThenBy(d => d.Article.Id, StringComparer.Ordinal)
                .Take(query.TopK)
                .Select(d => d.Article with { Score = Math.Round(d.Score, 6) })
                .ToList(),
        };
    }

    /// <summary>
    /// The scope entries a session may see beyond universal ("*") documents: families the
    /// deployment has enabled that also match one of the session's scope tokens. Same rule as
    /// <see cref="KnowledgeIndex"/>; empty means universal documents only (fail closed).
    /// </summary>
    public static IReadOnlyCollection<string> AllowedScopes(IReadOnlySet<string> scopeTokens, IEnumerable<string> enabledFamilies) =>
        scopeTokens.Count == 0
            ? []
            : enabledFamilies
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Where(entry => scopeTokens.Contains(entry)
                    || scopeTokens.Any(token =>
                        token.Contains(entry, StringComparison.OrdinalIgnoreCase)
                        || entry.Contains(token, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
}
