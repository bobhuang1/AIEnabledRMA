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
    }
}
