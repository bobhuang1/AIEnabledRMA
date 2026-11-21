namespace AIEnabledRma.Rag.Store;

/// <summary>
/// The embedding model could not be reached while a query was being embedded.
/// </summary>
/// <remarks>
/// This is an outage, not a data problem: <see cref="EmbeddingModelMismatchException"/> means the
/// store holds vectors from another model and only a re-embed fixes it, while this means the model
/// that would answer is down or absent — a local Ollama server that was never started, or a model
/// that was never pulled. The retriever can survive it by serving the lexical index, but it must say
/// so: silently answering from the lexical index is indistinguishable from a working hybrid store,
/// which is how a local setup degrades without anyone noticing.
/// </remarks>
public sealed class EmbeddingUnavailableException(string modelName, Exception inner)
    : InvalidOperationException(
        $"The embedding model '{modelName}' could not be reached: {inner.Message}",
        inner)
{
    /// <summary>The model that was asked for, as the store records it (<c>ollama:nomic-embed-text</c>).</summary>
    public string ModelName { get; } = modelName;
}
