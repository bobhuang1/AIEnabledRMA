using AIEnabledRma.Rag.Knowledge;

namespace AIEnabledRma.Tests;

/// <summary>
/// Pins the two reductions the golden set's misses turned on.
/// </summary>
/// <remarks>
/// These are assertions about behaviour a retrieval change is allowed to improve, not about spelling:
/// if a later tokeniser is smarter and these fail, the question is whether the golden set still measures
/// what it measures, not whether the test is stale. Each case below is a phrase from
/// <c>eval/retrieval-golden.json</c> and the tokens the index needs for it to have a chance.
/// </remarks>
public sealed class TextTokensTests
{
    [Fact]
    public void Function_words_are_not_scored_terms()
    {
        // rq-001 and rq-022 in miniature: without this, "the", "but" and "now" compete with content.
        // The stem and the surface form both survive ("light" and "lights"): one lets the singular
        // match, the other keeps an exact word exact.
        Assert.Equal(
            ["screen", "black", "light", "lights"],
            TextTokens.Tokenize("the screen is black but the lights are still on"));
    }

    [Fact]
    public void An_inflection_matches_the_base_word_the_article_uses()
    {
        // rq-012: the article says "spill"; the customer wrote "spilled". The surface form is kept
        // beside the stem, so both directions match and an exact word stays exact.
        var tokens = TextTokens.Tokenize("coffee spilled into the unit and now it misbehaves");
        Assert.Contains("spill", tokens);
        Assert.Contains("spilled", tokens);
        Assert.Contains("unit", tokens);
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("now", tokens);

        // rq-010: "scratch" against the article's "scratched", and the reverse direction.
        Assert.Equal(TextTokens.Stem("scratch"), TextTokens.Stem("scratched"));
        Assert.Equal(TextTokens.Stem("spill"), TextTokens.Stem("spilled"));
        Assert.Equal(TextTokens.Stem("charge"), TextTokens.Stem("charging"));
        Assert.Equal(TextTokens.Stem("charge"), TextTokens.Stem("charges"));
        Assert.Equal(TextTokens.Stem("update"), TextTokens.Stem("updated"));
        Assert.Equal(TextTokens.Stem("drop"), TextTokens.Stem("dropped"));
        Assert.Equal(TextTokens.Stem("cable"), TextTokens.Stem("cables"));
        Assert.Equal(TextTokens.Stem("case"), TextTokens.Stem("cases"));
        Assert.Equal(TextTokens.Stem("battery"), TextTokens.Stem("batteries"));
        Assert.Equal(TextTokens.Stem("glass"), TextTokens.Stem("glasses"));
    }

    [Theory]
    [InlineData("spill")]
    [InlineData("small")]
    [InlineData("well")]
    [InlineData("light")]
    [InlineData("status")]
    [InlineData("analysis")]
    public void A_word_that_is_already_a_base_form_is_left_alone(string word)
    {
        // The doubled consonant collapse is only for consonants that do not end an English word
        // doubled: "spilled" must not become "spil".
        Assert.Equal(word, TextTokens.Tokenize(word).Single());
    }

    [Fact]
    public void Negation_survives()
    {
        // "not" is a stopword-shaped word that changes the answer, so it is kept.
        Assert.Contains("not", TextTokens.Tokenize("it will not switch on"));
        Assert.DoesNotContain("will", TextTokens.Tokenize("it will not switch on"));
    }
}
