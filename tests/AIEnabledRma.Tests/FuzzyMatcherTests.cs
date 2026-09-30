using AIEnabledRma.Domain.Devices;

namespace AIEnabledRma.Tests;

/// <summary>
/// The score produced here decides whether a serial number is treated as a hit. A matcher
/// that is too eager returns the wrong customer's device, and a returns agent who cannot see
/// that mistake will ship a replacement to it. These tests pin both ends: the tolerance that
/// makes support bearable, and the restraint that keeps unrelated strings apart.
/// </summary>
public sealed class FuzzyMatcherTests
{
    // ---------- normalisation ----------

    [Fact]
    public void Punctuation_and_spacing_do_not_defeat_a_match() =>
        Assert.Equal(1d, FuzzyMatcher.Score("O'Brien", "o brien"), 6);

    [Fact]
    public void Case_is_irrelevant() =>
        Assert.Equal(1d, FuzzyMatcher.Score("VERTEX-100", "vertex-100"), 6);

    [Fact]
    public void Tokenisation_splits_on_non_alphanumerics() =>
        Assert.Equal(["acme", "corp"], FuzzyMatcher.Tokenize("Acme-Corp."));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!@#$")]
    public void Input_with_nothing_matchable_scores_zero(string? query) =>
        Assert.Equal(0d, FuzzyMatcher.Score(query, "vertex-100"), 6);

    [Fact]
    public void An_absent_candidate_scores_zero() =>
        Assert.Equal(0d, FuzzyMatcher.Score("vertex-100", null), 6);

    // ---------- the ladder of matches ----------

    [Fact]
    public void An_exact_match_scores_one() =>
        Assert.Equal(1d, FuzzyMatcher.Score("vertex-100", "vertex-100"), 6);

    [Fact]
    public void A_prefix_scores_well_but_below_an_exact_hit() =>
        // Agents routinely type the first few characters of a serial. This must rank below a
        // real match so a prefix never outranks the actual device.
        Assert.Equal(0.9d, FuzzyMatcher.Score("vertex", "vertex-100"), 6);

    [Fact]
    public void Every_query_token_present_scores_well()
    {
        // Deliberately not in prefix position: "vertex 100" against "vertex 100 pro" is a
        // prefix of the squashed string and would score 0.9 instead. This one exercises the
        // token-containment rule itself.
        Assert.Equal(0.85d, FuzzyMatcher.Score("100 vertex", "vertex 100 pro"), 6);
    }

    [Fact]
    public void Reordered_tokens_still_match() =>
        // Levenshtein over the squashed string catches "100 vertex" for "vertex 100".
        Assert.True(FuzzyMatcher.Score("100 vertex", "vertex 100") > 0.8);

    [Fact]
    public void A_single_transposed_character_still_matches() =>
        // The classic serial misread: 1 and l, 0 and O. Support cannot read them apart.
        Assert.True(FuzzyMatcher.Score("AX1-0001", "AXI-0001") > 0.5);

    [Fact]
    public void A_one_character_typo_still_matches() =>
        Assert.True(FuzzyMatcher.Score("vertex-100", "vertex-l00") > 0.5);

    // ---------- restraint ----------

    [Fact]
    public void An_unrelated_string_scores_low()
    {
        // Two different products must not be confused. If this creeps up, the lookup tool
        // starts returning the wrong unit.
        Assert.True(
            FuzzyMatcher.Score("vertex-100", "nimbus-9000") < 0.5,
            $"scored {FuzzyMatcher.Score("vertex-100", "nimbus-9000")}");
    }

    [Fact]
    public void Two_different_serials_of_the_same_length_score_low()
    {
        // The dangerous case: same product, adjacent serial numbers, both in the catalog.
        Assert.True(
            FuzzyMatcher.Score("SN-0001", "SN-0002") < 0.9,
            $"scored {FuzzyMatcher.Score("SN-0001", "SN-0002")}");
    }

    [Fact]
    public void A_partial_serial_never_scores_as_an_exact_hit() =>
        Assert.True(FuzzyMatcher.Score("SN-00", "SN-0001") < 1d);

    [Fact]
    public void The_score_never_exceeds_one() =>
        Assert.InRange(FuzzyMatcher.Score("vertex-100", "vertex-100"), 0d, 1d);

    [Fact]
    public void The_score_is_symmetric_enough_to_be_intuitive()
    {
        // Not a strict mathematical requirement, but a matcher whose score depends mostly on
        // argument order is hard to reason about when tuning a threshold.
        var forward = FuzzyMatcher.Score("josephin smyth", "josephine smith");
        var backward = FuzzyMatcher.Score("josephine smith", "josephin smyth");

        Assert.Equal(forward, backward, 2);
    }

    // ---------- BestScore across fields ----------

    [Fact]
    public void BestScore_picks_the_strongest_field()
    {
        // Name, email, and phone are all compared; the phone is the exact one here.
        var score = FuzzyMatcher.BestScore(
            "+44 20 7946 0001", "Ada Lovelace", "ada@example.com", "+442079460001");

        Assert.True(score > 0.9, $"scored {score}");
    }

    [Fact]
    public void BestScore_ignores_null_fields() =>
        Assert.True(FuzzyMatcher.BestScore("ada", null, "Ada Lovelace", "x@y.z") > 0.5);

    // ---------- Levenshtein ----------

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("abcd", "bcda", 2)]
    public void Levenshtein_known_distances(string left, string right, int expected) =>
        Assert.Equal(expected, FuzzyMatcher.Levenshtein(left, right));

    [Fact]
    public void Levenshtein_is_symmetric() =>
        Assert.Equal(
            FuzzyMatcher.Levenshtein("kitten", "sitting"),
            FuzzyMatcher.Levenshtein("sitting", "kitten"));
}
