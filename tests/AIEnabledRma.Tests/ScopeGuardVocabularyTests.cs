using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Triage;

namespace AIEnabledRma.Tests;

/// <summary>
/// The guard's vocabulary is derived from the corpus, so these cases pin the derivation itself:
/// which words it takes (the categories and keywords an author already wrote), which it drops
/// (English function words that would otherwise count as troubleshooting terms), and the one
/// case it refuses to produce at all.
/// </summary>
public sealed class ScopeGuardVocabularyTests
{
    private static ScopeVocabularyArticle Article(string category, params string[] keywords) => new()
    {
        Category = category,
        Keywords = keywords,
    };

    [Fact]
    public void The_vocabulary_is_the_categories_and_keywords_the_corpus_declares()
    {
        var vocabularies = ScopeGuardVocabulary.Derive(
        [
            Article("power", "battery", "charging"),
            Article("display", "screen", "blank"),
        ]);

        // One vocabulary per category, ordered by category name so the result is stable.
        Assert.Equal(2, vocabularies.Count);
        Assert.Contains("display", vocabularies[0]);
        Assert.Contains("screen", vocabularies[0]);
        Assert.Contains("power", vocabularies[1]);
        Assert.Contains("battery", vocabularies[1]);

        // Categories do not bleed into each other: the display words are not power words.
        Assert.DoesNotContain("screen", vocabularies[1]);
        Assert.DoesNotContain("battery", vocabularies[0]);
    }

    [Fact]
    public void Keyword_phrases_are_split_so_the_words_a_customer_types_are_what_match()
    {
        var vocabularies = ScopeGuardVocabulary.Derive(
            [Article("power", "will not boot", "no power")]);

        Assert.Contains("boot", vocabularies[0]);
        Assert.Contains("power", vocabularies[0]);

        // The glue around them is not a topic term: kept, it would score any sentence that
        // happens to contain "will" or "not" as partly on-topic.
        Assert.DoesNotContain("will", vocabularies[0]);
        Assert.DoesNotContain("not", vocabularies[0]);

        // The filter covers every form the tokeniser produces for a filtered word, not the one spelling
        // the list holds: "please" reaches the guard as "please" and "pleas", and reducing those leaves
        // "plea" — a form a filter that knew the word only as written would keep as a topic term.
        Assert.Equal(["power"], ScopeGuardVocabulary.Derive([Article("power", "please")]).Single());
    }

    /// <summary>
    /// The guard's side of the same rule: a word belongs to the domain however it is inflected. This
    /// is the eval's <c>tq-014</c> in miniature — kb-0014 declares <c>reboot</c>, the customer writes
    /// <c>reboots</c> — and the reason the guard and the retriever share one tokeniser.
    /// </summary>
    [Fact]
    public void A_keyword_is_matched_in_its_inflections()
    {
        var guard = new ScopeGuard(
            new ScopeGuardOptions(),
            ScopeGuardVocabulary.Derive([Article("power", "reboot", "unit")]));

        var result = guard.Evaluate(Request("the unit reboots by itself every few minutes"), [Article()]);

        Assert.True(result.InScope, $"the guard refused an inflection of its own keyword ({result.Reason})");

        // The other direction: the keyword is inflected and the customer's word is not. A guard that
        // only stripped suffixes would pass the first assertion and fail this one.
        var other = new ScopeGuard(
            new ScopeGuardOptions(),
            ScopeGuardVocabulary.Derive([Article("power", "restarts")]));

        Assert.True(other.Evaluate(Request("the unit restarted on its own"), [Article()]).InScope);

        // A stem is shared by a word's inflections, not by topics: a sentence that shares none of the
        // vocabulary's is still refused, so the fix cannot pass by accepting everything.
        var offTopic = guard.Evaluate(Request("please write me a poem about the ocean"), [Article()]);

        Assert.False(offTopic.InScope);
        Assert.Equal("off-topic", offTopic.Reason);
    }

    [Fact]
    public void A_corpus_with_nothing_to_derive_from_cannot_build_a_guard_at_all()
    {
        var vocabulary = ScopeGuardVocabulary.Derive([]);

        Assert.Empty(vocabulary);

        // The refusal lives here, where the guard is built, rather than in the derivation: a
        // guard with no words answers every question with "off-topic" and nothing looks wrong.
        var thrown = Assert.Throws<ArgumentException>(
            () => new ScopeGuard(new ScopeGuardOptions(), vocabulary));

        Assert.Contains("topic word", thrown.Message, StringComparison.OrdinalIgnoreCase);

        // Words that carry no topic signal are the same guard, however many of them there are.
        Assert.Throws<ArgumentException>(
            () => new ScopeGuard(new ScopeGuardOptions(), [["will", "not", "these"]]));
    }

    private static TriageRequest Request(string statement) => new()
    {
        Devices =
        [
            new TriageDeviceContext
            {
                SerialNumber = "SG-0001",
                ProductName = "Vertex Widget",
                Model = "SG",
                Sku = "SG-0001",
                IsInWarranty = true,
                ScopeTokens = new HashSet<string>(["Vertex Widget"], StringComparer.OrdinalIgnoreCase),
            },
        ],
        Conversation = [],
        CustomerStatement = statement,
        CorrelationId = "scope-guard-inflection",
    };

    private static RetrievedArticle Article() => new()
    {
        Id = "kb-0014",
        Title = "Unit reboots by itself",
        Category = "power",
        Score = 0.9,
        Snippet = "Check the firmware before the supply.",
    };
}
