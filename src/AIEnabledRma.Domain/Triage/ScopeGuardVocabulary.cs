using System.Text.RegularExpressions;

namespace AIEnabledRma.Domain.Triage;

/// <summary>
/// One article's contribution to the scope guard's vocabulary: the category it is filed under
/// and the keywords its author declared.
/// </summary>
public sealed record ScopeVocabularyArticle
{
    public required string Category { get; init; }

    /// <summary>
    /// The words the article declares it is about. Titles are deliberately not included: a
    /// title is prose for a human reader, so "Reading the charge indicator light" would offer
    /// "reading" as a domain term, while the author's keyword list already states which words
    /// the article answers to.
    /// </summary>
    public required IReadOnlyList<string> Keywords { get; init; }
}

/// <summary>
/// Builds the scope guard's topic vocabulary from the knowledge base instead of from a list
/// maintained by hand.
/// </summary>
/// <remarks>
/// The hand-written list this replaces ran to 124 words and still refused ordinary phrasings.
/// The reason is structural: a separate word list is a second description of the domain, and it
/// drifts from the corpus the moment either one is edited. The corpus already says which words
/// belong to the domain — every article declares a category and a keyword list — so the
/// vocabulary is read from it, one vocabulary per category. An article added tomorrow
/// contributes its words on the next start with no configuration edit, and a word no article
/// claims is not silently treated as on-topic.
///
/// Keyword lists are written as phrases ("will not boot", "no power"), and splitting them is
/// what lets the guard match how a customer actually writes. The English function words inside
/// those phrases are dropped: kept, "will" and "not" would be taught to the guard as
/// troubleshooting terms, which inflates the ratio for any sentence that happens to use them.
///
/// This is a pure function of the corpus. It returns nothing for an empty corpus rather than
/// throwing, because the refusal belongs where the guard is built: see the
/// <see cref="ScopeGuard"/> constructor.
/// </remarks>
public static partial class ScopeGuardVocabulary
{
    /// <summary>
    /// Words that carry no topic signal. Kept to the closed class of English function words:
    /// pronouns, auxiliaries, prepositions, determiners, and quantifiers. Domain words are
    /// never filtered here, however generic they look, because the guard's whole job is to
    /// decide whether a question is about the domain.
    /// </summary>
    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "also", "and", "any", "are", "but", "can", "could", "did", "does", "each", "for",
        "from", "had", "has", "have", "here", "how", "into", "just", "may", "might", "more",
        "most", "much", "must", "not", "only", "other", "our", "please", "should", "some",
        "such", "than", "that", "the", "their", "them", "then", "there", "these", "they", "this",
        "those", "very", "was", "were", "what", "when", "where", "which", "who", "why", "will",
        "with", "would", "you", "your",
    };

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    /// <summary>
    /// The topic vocabulary, grouped one vocabulary per knowledge-base category, each holding
    /// that category's name and the tokens of its articles' keywords. The guard accepts a word
    /// if any vocabulary contains it, so the grouping is for inspection rather than scoring:
    /// a question may mix a display problem with a warranty question and match both.
    /// </summary>
    public static List<List<string>> Derive(IEnumerable<ScopeVocabularyArticle> articles)
    {
        ArgumentNullException.ThrowIfNull(articles);

        return articles
            .GroupBy(
                a => a.Category.Trim().ToLowerInvariant(),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var words = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { group.Key };

                foreach (var token in group.SelectMany(a => a.Keywords).SelectMany(Tokenize))
                {
                    words.Add(token);
                }

                return words.ToList();
            })
            .ToList();
    }

    private static IEnumerable<string> Tokenize(string keyword) =>
        TokenRegex().Matches(keyword ?? string.Empty)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(token => token.Length > 2 && !FunctionWords.Contains(token));
}
