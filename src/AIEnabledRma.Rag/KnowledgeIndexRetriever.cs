using AIEnabledRma.Domain.Abstractions;

namespace AIEnabledRma.Rag;

/// <summary>
/// Adapts the in-process <see cref="KnowledgeIndex"/> to the domain's
/// <see cref="IKnowledgeRetriever"/> seam.
/// </summary>
/// <remarks>
/// This is the seam <see cref="IKnowledgeRetriever"/> was documented for. The index ranks and
/// scopes articles deterministically in memory, so retrieval is a pure function of the
/// currently published corpus; there is no network hop and nothing to retry.
///
/// It takes the holder rather than a <see cref="KnowledgeIndex"/> so a corpus reload is picked
/// up without re-registering anything. Injecting the index directly would capture one instance
/// and silently keep serving the pre-reload corpus.
/// </remarks>
public sealed class KnowledgeIndexRetriever(KnowledgeIndexHolder holder) : IKnowledgeRetriever
{
    public Task<KnowledgeRetrievalResult> RetrieveAsync(
        KnowledgeRetrievalQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(holder.Current.Search(query));
    }
}
