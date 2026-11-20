using System.Text.RegularExpressions;

namespace AIEnabledRma.Domain.Knowledge;

/// <summary>
/// Turns text into vectors for the knowledge store. Implementations are batched by the caller;
/// one call embeds one batch.
/// </summary>
public interface IEmbeddingModel
{
    /// <summary>Stable name, stored beside every vector so a model change forces a re-embed.</summary>
    string Name { get; }

    int Dimensions { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic, offline embedding by signed feature hashing of word unigrams and bigrams,
/// L2-normalised. It captures lexical overlap only (no synonyms), which is exactly what CI and the
/// offline demo need: reproducible vectors with no model download and no network call. Real
/// deployments configure a local embedding model instead (plan D4).
/// </summary>
public sealed partial class HashingEmbeddingModel(int dimensions = 384) : IEmbeddingModel
{
    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    public string Name => $"hashing-v1-{Dimensions}";

    public int Dimensions { get; } = dimensions > 0 ? dimensions : throw new ArgumentOutOfRangeException(nameof(dimensions));

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());
    }

    public float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        var words = WordRegex().Matches((text ?? string.Empty).ToLowerInvariant())
            .Select(m => Normalise(m.Value))
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .ToList();

        for (var i = 0; i < words.Count; i++)
        {
            Add(vector, words[i], 1f);
            if (i + 1 < words.Count)
            {
                Add(vector, words[i] + " " + words[i + 1], 0.5f);
            }
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0f)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    private void Add(float[] vector, string feature, float weight)
    {
        var hash = Fnv1a(feature);
        var index = (int)(hash % (uint)Dimensions);
        var sign = (hash & 0x80000000u) == 0 ? 1f : -1f;
        vector[index] += sign * weight;
    }

    // A crude plural/suffix fold so "charges" and "charging" land near "charge".
    private static string Normalise(string word) =>
        word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal) ? word[..^3]
        : word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal) ? word[..^2]
        : word.Length > 3 && word.EndsWith('s') ? word[..^1]
        : word;

    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "but", "not", "you", "your", "are", "was", "has",
        "have", "from", "they", "will", "can", "its", "into", "when", "what", "then", "than", "any",
        "all", "out", "one", "our", "too", "very", "just", "also", "been", "were", "does", "did",
    };
}
