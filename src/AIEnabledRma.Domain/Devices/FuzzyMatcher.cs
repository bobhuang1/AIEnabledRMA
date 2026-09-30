using System.Text.RegularExpressions;

namespace AIEnabledRma.Domain.Devices;

/// <summary>
/// Deterministic fuzzy string matching used for customer and device lookup.
/// The data layer adds PostgreSQL trigram ranking on top of this for large tables;
/// this scorer is what runs in-memory, in tests, and in the fallback path.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="candidate"/> in the range 0..1.
    /// Combines, in order of weight: exact match, prefix match, whole-token containment,
    /// token-set overlap, then character-level similarity. Empty queries score 0.
    /// </summary>
    public static double Score(string? query, string? candidate)
    {
        var q = Tokenize(query);
        var c = Tokenize(candidate);

        if (q.Count == 0 || c.Count == 0)
        {
            return 0d;
        }

        if (c.SequenceEqual(q, StringComparer.OrdinalIgnoreCase))
        {
            return 1d;
        }

        var cJoined = string.Concat(c);
        var qJoined = string.Concat(q);

        if (cJoined.Equals(qJoined, StringComparison.OrdinalIgnoreCase))
        {
            return 0.98d;
        }

        // Prefix of the whole string, e.g. "acme" against "acme corp".
        if (cJoined.StartsWith(qJoined, StringComparison.OrdinalIgnoreCase))
        {
            return 0.9d;
        }

        // Every query token appears somewhere in the candidate.
        if (q.All(qt => c.Contains(qt, StringComparer.OrdinalIgnoreCase)))
        {
            return 0.85d;
        }

        // Some token overlap, scaled by how much of the candidate was covered.
        var overlap = q.Count(qt => c.Contains(qt, StringComparer.OrdinalIgnoreCase));
        var tokenScore = (double)overlap / Math.Max(q.Count, c.Count);

        // Character-level similarity over the squashed strings, to catch transpositions and
        // single-character typos that token overlap misses entirely.
        var editScore = 1d - (double)Levenshtein(qJoined, cJoined) / Math.Max(qJoined.Length, cJoined.Length);

        return Math.Clamp((tokenScore * 0.6d) + (editScore * 0.4d), 0d, 0.89d);
    }

    /// <summary>Best score across several fields, e.g. name-or-email lookup.</summary>
    public static double BestScore(string? query, params string?[] candidates) =>
        candidates.Max(c => Score(query, c));

    /// <summary>
    /// Standard iterative Levenshtein distance with a rolling row, so memory stays O(min(n,m)).
    /// </summary>
    public static int Levenshtein(string? left, string? right)
    {
        left ??= string.Empty;
        right ??= string.Empty;

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        if (left.Length > right.Length)
        {
            (left, right) = (right, left);
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    /// <summary>
    /// Lower-cases and splits on everything that is not a letter or digit. This is what makes
    /// "O'Brien", "o brien", and "OBRIEN" compare equal, and keeps punctuation and spacing
    /// differences from defeating the match.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : Regex.Matches(value.ToLowerInvariant(), "[a-z0-9]+")
                .Select(m => m.Value)
                .ToArray();
}
