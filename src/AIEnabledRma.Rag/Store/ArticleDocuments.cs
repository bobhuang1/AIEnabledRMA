using System.Security.Cryptography;
using System.Text;
using AIEnabledRma.Rag.Knowledge;

namespace AIEnabledRma.Rag.Store;

/// <summary>Turns loaded knowledge-base articles into chunked documents for the store.</summary>
public static class ArticleDocuments
{
    public static IReadOnlyList<KbDocumentInput> From(KnowledgeBaseLoadResult loaded, ChunkingOptions? chunking = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        if (loaded.Errors.Count > 0)
        {
            // The same rule as a reload: a corpus with load errors is not published.
            throw new InvalidOperationException(
                "The knowledge base has load errors and will not be ingested:\n  " + string.Join("\n  ", loaded.Errors));
        }

        return loaded.Articles
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new KbDocumentInput
            {
                SourceId = a.Id,
                Kind = KbKinds.Article,
                Title = a.Title,
                Category = a.Category,
                ProductScope = a.ProductScope,
                ReviewedBy = a.ReviewedBy,
                ReviewedAt = a.ReviewedAt,
                ContentHash = DocumentHash(a),
                Chunks = MarkdownChunker.Chunk(a.Title, a.Body, chunking),
            })
            .ToList();
    }

    /// <summary>
    /// Changes to anything that affects retrieval (body, title, category, scope, review) change the
    /// hash, so a re-ingest notices them.
    /// </summary>
    public static string DocumentHash(KnowledgeArticle article)
    {
        var material = string.Join(
            "\n",
            article.ContentHash,
            article.Title,
            article.Category,
            string.Join(",", article.ProductScope),
            article.ReviewedBy,
            article.ReviewedAt?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
