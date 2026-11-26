using AIEnabledRma.Domain.Knowledge;

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
/// The split is <see cref="TextTokens"/>, the rule the retriever already indexes the corpus and
/// the question with, which also gives every word its stem. That matters beyond tidiness: the
/// eval's <c>tq-014</c> — "the unit reboots by itself every few minutes", which is <c>kb-0014</c>'s
/// own symptom — was refused as off-topic because this file compared words as written while the
/// article's keyword is "reboot". One word belongs to the domain however it is inflected, and the
/// guard now says so the same way retrieval does.
///
/// This is a pure function of the corpus. It returns nothing for an empty corpus rather than
/// throwing, because the refusal belongs where the guard is built: see the
/// <see cref="ScopeGuard"/> constructor.
/// </remarks>
public static class ScopeGuardVocabulary
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

    /// <summary>
    /// The forms those words arrive in, which is what has to be filtered. The tokeniser hands over each
    /// word beside its stem, so "please" arrives as "please" and "pleas", and reducing those again
    /// leaves "plea": three spellings of one word that is not a topic. Taking the set from the tokeniser
    /// rather than guessing at it is what keeps the filter and the stream it filters the same shape.
    /// </summary>
    private static readonly HashSet<string> FunctionWordStems = FunctionWords
        .SelectMany(TextTokens.Tokenize)
        .Select(TextTokens.Stem)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The topic vocabulary, grouped one vocabulary per knowledge-base category, each holding
    /// that category's name and its articles' keywords in the form the guard compares them —
    /// each word reduced to its stem. The guard accepts a word if any vocabulary contains it, so
    /// the grouping is for inspection rather than scoring: a question may mix a display problem
    /// with a warranty question and match both.
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

                foreach (var token in group.SelectMany(a => a.Keywords).SelectMany(Tokens))
                {
                    words.Add(token);
                }

                return words.ToList();
            })
            .ToList();
    }

    /// <summary>
    /// The topic words in a piece of text, each reduced to the stem the guard compares: the retrieval
    /// tokeniser's output — lower case, split, stopwords dropped, every word beside its stem — with
    /// the guard's own function words removed and the two forms of an inflected word collapsed back
    /// to one. One function for both sides of the comparison, because a vocabulary reduced one way
    /// and a question reduced another is measuring its own inconsistency.
    /// </summary>
    /// <remarks>
    /// Collapsing matters to the ratio and not only to tidiness: the tokeniser hands back both
    /// <c>charge</c> and <c>charging</c>, so a sentence using two inflections of one keyword would
    /// otherwise be two pieces of evidence while the vocabulary's one word stayed one.
    /// </remarks>
    internal static IReadOnlyList<string> Tokens(string? text) =>
        TextTokens.Tokenize(text)
            .Select(TextTokens.Stem)
            .Where(token => !FunctionWordStems.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
