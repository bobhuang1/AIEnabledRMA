using System.Text.RegularExpressions;

namespace AIEnabledRma.Rag.Knowledge;

/// <summary>
/// The one tokenisation rule, used for article text and for the question, because a retriever that
/// reduces one side differently is measuring its own inconsistency.
/// </summary>
/// <remarks>
/// Three deliberate reductions, each of which the golden set showed was needed:
/// <list type="bullet">
/// <item>lower case and split on anything that is not a letter or digit, dropping one- and two-letter
/// tokens — the original rule, unchanged;</item>
/// <item>a stopword list, because "the", "and" and "now" are not evidence of anything and were scored
/// terms: a question phrased as a sentence competed on its grammar against articles that happened to
/// contain the same grammar;</item>
/// <item>a small suffix stemmer, because a customer writes "spilled" and "scratched" while the article
/// says "spill" and "scratch", and no amount of idf bridges that.</item>
/// </list>
/// The stemmer is deliberately shallow. It handles inflection (-s, -es, -ies, -ed, -ing, and the
/// doubled consonant an inflection leaves behind) and nothing else: no derivational rules, no
/// dictionary, no attempt at linguistics. Every rule is pinned by <c>TextTokensTests</c>, because a
/// stemmer that quietly over-reaches is a retrieval regression that looks like an improvement until
/// the golden set is run.
/// </remarks>
public static partial class TextTokens
{
    /// <summary>
    /// Common English function words. Not exhaustive: it covers the words that appear in the corpus's
    /// own prose and in the golden set's questions. Adding to it is cheap; adding a word that carries
    /// meaning ("not", "off") is not, so those stay.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "but", "for", "with", "that", "this", "these", "those", "there", "here",
        "what", "when", "where", "which", "while", "who", "why", "how",
        "are", "was", "were", "been", "being", "has", "have", "had", "does", "did", "doing",
        "will", "would", "can", "could", "should", "shall", "may", "might", "must",
        "into", "onto", "over", "under", "again", "just", "also", "then", "than", "now",
        "your", "you", "our", "their", "them", "they", "his", "her", "she", "its",
        "all", "any", "some", "each", "more", "most", "much", "very", "too", "only", "same",
        "such", "nor", "out", "off", "about", "because", "both", "few", "other", "through",
        "between", "against", "above", "below", "before", "after", "during", "once", "from",
        "one", "two", "get", "got", "still",
    };

    /// <summary>
    /// Splits, drops stopwords, and reduces each surviving word to both its surface form and its stem.
    /// </summary>
    /// <remarks>
    /// Both, not just the stem, and the reason is measured: stemming alone lost the golden set's
    /// "it restarts by itself every few minutes" case, because the article's own keyword is
    /// <c>restarts</c> and collapsing it to <c>restart</c> let a shorter article that uses the same
    /// stem win on its other words. Indexing the surface form keeps exact matches exact; indexing the
    /// stem is what lets "spilled" reach "spill". The cost is a larger token set per article, which
    /// BM25's length normalisation absorbs.
    /// </remarks>
    public static IReadOnlyList<string> Tokenize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : WordPattern()
                .Matches(value.ToLowerInvariant())
                .Select(m => m.Value)
                .Where(token => token.Length > 2)
                .Where(token => !Stopwords.Contains(token))
                .SelectMany(Forms)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>The word as written, and its stem when that differs.</summary>
    private static IEnumerable<string> Forms(string token)
    {
        var stem = Stem(token);

        if (string.Equals(stem, token, StringComparison.Ordinal))
        {
            return [token];
        }

        return [stem, token];
    }

    /// <summary>
    /// Shallow suffix stripping. The minimum stem length is three, so "is"/"was" cannot become empty
    /// and short words are left alone rather than mangled.
    /// </summary>
    public static string Stem(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (token.Length <= 3)
        {
            return token;
        }

        return DropFinalE(Strip(token));
    }

    /// <summary>
    /// Removes one trailing "e", after the suffix rules have run. Without it "charge" and "charging"
    /// reduce to different strings, and a corpus that writes both would never match the customer's
    /// one.
    /// </summary>
    private static string DropFinalE(string stem) =>
        stem.Length > 3 && stem.EndsWith("e", StringComparison.Ordinal) ? stem[..^1] : stem;

    private static string Strip(string token)
    {

        // glasses -> glass. Done before the plural rule, which would otherwise leave "glasse".
        if (token.EndsWith("sses", StringComparison.Ordinal) && token.Length > 5)
        {
            return token[..^2];
        }

        // batteries -> battery, policies -> policy.
        if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 5)
        {
            return token[..^3] + "y";
        }

        // charging -> charg, spilling -> spill.
        if (token.EndsWith("ing", StringComparison.Ordinal) && token.Length > 5)
        {
            return Complete(TrimDoubled(token[..^3]));
        }

        // spilled -> spill, scratched -> scratch, dropped -> drop (via the doubled consonant).
        if (token.EndsWith("ed", StringComparison.Ordinal) && token.Length > 4)
        {
            return Complete(TrimDoubled(token[..^2]));
        }

        // charges -> charg, crashes -> crash, boxes -> box: an -es that follows a sibilant is a suffix.
        if (token.EndsWith("es", StringComparison.Ordinal) && token.Length > 4 && EndsInSibilant(token))
        {
            return token[..^2];
        }

        // lights -> light, cases -> case, drops -> drop.
        if (token.EndsWith("s", StringComparison.Ordinal)
            && token.Length > 3
            && !token.EndsWith("ss", StringComparison.Ordinal)
            && !token.EndsWith("us", StringComparison.Ordinal)
            && !token.EndsWith("is", StringComparison.Ordinal))
        {
            return token[..^1];
        }

        return token;
    }

    /// <summary>
    /// Puts back the "e" that the suffix was hiding: "updated" strips to "updat", which would no longer
    /// match the article's own "update". The three endings are the ones where the silent "e" was
    /// dropped to make room for the suffix, and adding it anywhere else would invent words.
    /// </summary>
    private static string Complete(string stem) =>
        stem.EndsWith("at", StringComparison.Ordinal)
        || stem.EndsWith("bl", StringComparison.Ordinal)
        || stem.EndsWith("iz", StringComparison.Ordinal)
            ? stem + "e"
            : stem;

    private static bool EndsInSibilant(string token) =>
        token.EndsWith("ches", StringComparison.Ordinal)
        || token.EndsWith("shes", StringComparison.Ordinal)
        || token.EndsWith("sses", StringComparison.Ordinal)
        || token.EndsWith("xes", StringComparison.Ordinal)
        || token.EndsWith("zes", StringComparison.Ordinal);

    /// <summary>
    /// "dropped" loses its suffix and leaves "dropp"; "spilled" leaves "spill", which is a word. Only
    /// the consonants that do not end an English word doubled are collapsed, so "spill", "small" and
    /// "well" keep their second letter.
    /// </summary>
    private static string TrimDoubled(string stem) =>
        stem.Length > 3
        && stem[^1] == stem[^2]
        && "bdgmnprt".Contains(stem[^1], StringComparison.Ordinal)
            ? stem[..^1]
            : stem;

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordPattern();
}
