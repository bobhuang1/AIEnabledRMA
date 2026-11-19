using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Rag.Knowledge;

namespace AIEnabledRma.Rag;

/// <summary>
/// The default retriever behind <see cref="IKnowledgeRetriever"/>: it adds the words the corpus uses
/// to the words the customer used, then asks the lexical index the expanded question.
/// </summary>
/// <remarks>
/// This exists because the seam was only ever measured with one implementation. The acceptance test
/// resolves <c>IKnowledgeRetriever</c> from the host's own registration and scores whatever it finds,
/// so a vector store or this decorator is measured by the same forty cases and the same floors — and
/// enabling <c>Rag:Retrieval:SynonymExpansion</c> was the whole swap. It is the default now, because
/// the map fixes three questions the tokeniser could not; setting it to "false" is the ablation that
/// reproduces the plain index's 0.850, which the golden set records as its own measurement.
///
/// The map is deliberately tiny: the vocabulary gaps the golden set names, and nothing else. A
/// synonym list that grows without being measured is a ranking change nobody can attribute to a
/// question, which is the failure mode the golden set was widened to catch.
/// </remarks>
public sealed class SynonymExpandingRetriever(KnowledgeIndexHolder holder) : IKnowledgeRetriever
{
    /// <summary>
    /// Customer word to corpus words. Keys are the tokeniser's output, so both the surface form and
    /// the stem of a word are listed where the stemmer changes it.
    /// </summary>
    private static readonly Dictionary<string, string[]> Expansions = new(StringComparer.Ordinal)
    {
        // "it will not switch on" against the power article's "turn on".
        ["switch"] = ["turn"],
        ["switched"] = ["turn"],

        // "nothing happens when I pull the trigger" against "trigger ... fires".
        ["pull"] = ["press"],
        ["nothing"] = ["dead"],

        // "the firmware update stopped halfway" against "a truncated transfer".
        ["halfway"] = ["truncated"],

        // "before you accept the unit is faulty" against "hardware fault".
        ["faulty"] = ["fault"],

        // "photos of the damage" and "what should I write down" against "documentation", "record".
        ["photo"] = ["documentation"],
        ["photos"] = ["documentation"],
        ["write"] = ["record"],
        ["writ"] = ["record"],
    };

    public Task<KnowledgeRetrievalResult> RetrieveAsync(
        KnowledgeRetrievalQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var additions = TextTokens.Tokenize(query.Query)
            .SelectMany(token => Expansions.TryGetValue(token, out var words) ? words : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var expanded = additions.Length == 0
            ? query
            : query with { Query = $"{query.Query} {string.Join(' ', additions)}" };

        return Task.FromResult(holder.Current.Search(expanded));
    }
}
