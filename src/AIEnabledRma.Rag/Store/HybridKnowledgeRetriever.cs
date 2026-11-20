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
    KnowledgeIndexRetriever fallback,
    IOptions<VectorStoreOptions> options,
    IOptions<RagOptions> ragOptions,
    ILogger<HybridKnowledgeRetriever> logger) : IKnowledgeRetriever
{
    public async Task<KnowledgeRetrievalResult> RetrieveAsync(KnowledgeRetrievalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var allowed = AllowedScopes(query.ScopeTokens, ragOptions.Value.EnabledProductFamilies);

        IReadOnlyList<KbChunkHit> hits;
        try
        {
            hits = await store.SearchAsync(query.Query, allowed, options.Value.ChunkCandidates, cancellationToken);
            if (hits.Count == 0)
            {
                return new KnowledgeRetrievalResult
                {
                    Articles = [],
                    CorpusOutOfScope = await store.CountInScopeAsync(allowed, cancellationToken) == 0,
                };
            }
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

        var hints = query.CategoryHints.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var documents = hits
            .GroupBy(h => (h.Kind, h.SourceId))
            .Select(g =>
            {
                var best = g.OrderByDescending(h => h.Score).Take(Math.Max(1, options.Value.ChunksPerDocument)).ToList();

                // A small nudge for the device's own category, the same idea as the lexical
                // index's category boost: enough to break ties, not to outrank a better match.
                var boost = hints.Contains(best[0].Category) ? 0.25d / (PgVectorKnowledgeStore.RrfK + 1) : 0d;
                return (Best: best, Score: best[0].Score + boost);
            })
            .OrderByDescending(d => d.Score)
            .ThenBy(d => d.Best[0].SourceId, StringComparer.Ordinal)
            .Take(query.TopK)
            .ToList();

        return new KnowledgeRetrievalResult
        {
            Articles = documents
                .Select(d => new RetrievedArticle
                {
                    Id = d.Best[0].SourceId,
                    Title = d.Best[0].Title,
                    Category = d.Best[0].Category,
                    Score = Math.Round(d.Score, 6),
                    Snippet = string.Join(
                        "\n\n",
                        d.Best.OrderBy(h => h.Ordinal).Select(h => h.HeadingPath + "\n" + h.Text)),
                    SourceKind = d.Best[0].Kind,
                    ChunkRef = string.Join(",", d.Best.OrderBy(h => h.Ordinal).Select(h => $"{h.SourceId}#{h.Ordinal}")),
                    CitationLabel = d.Best[0].CitationLabel,
                })
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
