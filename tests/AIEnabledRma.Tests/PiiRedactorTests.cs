using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Privacy;

namespace AIEnabledRma.Tests;

/// <summary>
/// The redactor is the last thing between a customer's words and a model provider. These tests
/// are written the way an attacker would read it: a corpus of ordinary fault statements, each
/// carrying one planted value, and the assertion that the value cannot be found afterwards.
/// </summary>
public sealed class PiiRedactorTests
{
    private static readonly string[] Planted =
    [
        "ada.lovelace@example.com",
        "020 7946 0018",
        "4111 1111 1111 1111",
        "2210 Harrison Street",
        "ORD-2026-77812",
        "99-1234567",
        "Josephine Smith",
    ];

    private static readonly string[] Corpus =
    [
        "The unit will not power on. Email me at ada.lovelace@example.com if you need anything.",
        "Indicator stays dark. Call 020 7946 0018 in the afternoon.",
        "I paid with 4111 1111 1111 1111 last time and want the same card refunded.",
        "Please ship the replacement to 2210 Harrison Street, it is a business address.",
        "Order ORD-2026-77812 arrived without the power supply.",
        "My tax id is 99-1234567 if you need it for the invoice.",
        "I'm Josephine Smith and the serial AX2-0001-0005 still will not power on.",
    ];

    private static IPiiRedactionSession Session() =>
        new PiiRedactor().BeginSession(
            customerNames: ["Josephine Smith"],
            protectedValues: ["AX2-0001-0005", "Vertex Widget", "VW-100"]);

    [Fact]
    public void No_planted_value_survives_the_corpus()
    {
        var session = Session();

        foreach (var statement in Corpus)
        {
            var redacted = session.Redact(statement).Text;

            foreach (var value in Planted)
            {
                Assert.DoesNotContain(value, redacted, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_redacted_corpus_still_reads_as_a_fault_report()
    {
        // Over-redaction is a real cost: the model has to be able to answer "the light is dark".
        var redacted = Session().Redact(Corpus[0]).Text;

        Assert.Contains("The unit will not power on.", redacted, StringComparison.Ordinal);
        Assert.Contains("[EMAIL_1]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_device_serial_is_kept_because_policy_needs_it()
    {
        var redacted = Session().Redact(Corpus[6]).Text;

        Assert.Contains("AX2-0001-0005", redacted, StringComparison.Ordinal);
        Assert.Contains("[NAME_1]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Placeholders_are_stable_within_a_conversation_and_distinct_across_values()
    {
        var session = Session();

        var first = session.Redact("write to ada.lovelace@example.com").Text;
        var second = session.Redact("again, ada.lovelace@example.com please").Text;
        var other = session.Redact("or grace.hopper@example.com").Text;

        // Stability is what lets the model say "the address above" without being told it.
        Assert.Contains("[EMAIL_1]", first, StringComparison.Ordinal);
        Assert.Contains("[EMAIL_1]", second, StringComparison.Ordinal);
        Assert.Contains("[EMAIL_2]", other, StringComparison.Ordinal);
        Assert.DoesNotContain("[EMAIL_3]", other, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_number_must_pass_the_luhn_check_to_be_redacted()
    {
        var session = Session();

        var valid = session.Redact("card 4111 1111 1111 1111").Text;
        var invalid = session.Redact("build 4111 1111 1111 1112").Text;

        Assert.Contains("[CARD_1]", valid, StringComparison.Ordinal);
        Assert.DoesNotContain("4111 1111 1111 1111", valid, StringComparison.Ordinal);

        // The check digit is what keeps firmware builds and batch codes out of the redactor's
        // way; a rejection here is the feature, not a miss.
        Assert.Contains("4111 1111 1111 1112", invalid, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_or_a_year_is_not_mistaken_for_a_phone_number()
    {
        var session = Session();

        var redacted = session.Redact("it failed on 2026-09-29 and was bought in 2024").Text;

        Assert.Contains("2026-09-29", redacted, StringComparison.Ordinal);
        Assert.Contains("2024", redacted, StringComparison.Ordinal);
        Assert.Empty(session.Redact("it failed on 2026-09-29").Counts);
    }

    [Fact]
    public void The_result_reports_counts_by_kind_and_never_the_values()
    {
        var result = Session().Redact(Corpus[0] + " and 020 7946 0018");

        Assert.Equal(1, result.Counts["EMAIL"]);
        Assert.Equal(1, result.Counts["PHONE"]);
        Assert.Equal(2, result.TotalCount);

        // The report is logged. It must not be a second copy of what was redacted.
        var report = string.Join(';', result.Counts.Select(c => $"{c.Key}={c.Value}"));
        Assert.DoesNotContain("@", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Detection_finds_an_identifier_without_replacing_it()
    {
        var session = Session();

        Assert.True(session.ContainsIdentifiers("my email is ada.lovelace@example.com"));
        Assert.True(session.ContainsIdentifiers("ship to 2210 Harrison Street"));
        Assert.False(session.ContainsIdentifiers("the indicator light stays dark"));
        Assert.False(session.ContainsIdentifiers("serial AX2-0001-0005 is affected"));
    }

    // ---------- the deterministic name pass: a title or an introduction ----------

    /// <summary>
    /// A third party's name reaches a fault description with no customer record beside it, so the
    /// supplied-name pass cannot see it. A title is the one thing that makes the words after it a
    /// person by construction, and finding it needs no model.
    /// </summary>
    [Fact]
    public void A_title_makes_the_words_after_it_a_person()
    {
        var session = Session();

        var fitted = session.Redact("Mr Harrison fitted the replacement and it still will not power on.").Text;
        var confirmed = session.Redact("Dr. Ada Lovelace confirmed the indicator stays dark.").Text;

        Assert.Contains("[NAME_1]", fitted, StringComparison.Ordinal);
        Assert.DoesNotContain("Harrison", fitted, StringComparison.Ordinal);

        Assert.DoesNotContain("Ada", confirmed, StringComparison.Ordinal);
        Assert.DoesNotContain("Lovelace", confirmed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_self_introduction_names_a_person_with_no_customer_record()
    {
        var session = Session();

        var named = session.Redact("Hi, my name is Marcus Webb and the fan never spins.").Text;
        var contracted = session.Redact("I'm Priya Raman. The unit is dead.").Text;

        Assert.Contains("[NAME_1]", named, StringComparison.Ordinal);
        Assert.DoesNotContain("Marcus", named, StringComparison.Ordinal);
        Assert.DoesNotContain("Webb", named, StringComparison.Ordinal);

        Assert.DoesNotContain("Priya", contracted, StringComparison.Ordinal);
        Assert.DoesNotContain("Raman", contracted, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Mr McDonald" and "I'm Anne-Marie O'Brien": the capital is not always the word's first
    /// letter, and a name word that stopped at it left the person as typed.
    /// </summary>
    /// <remarks>
    /// The old word was one `[A-Z][a-z]+`, which cannot span the internal capital of `McDonald` or
    /// the apostrophe after a lone `O`; the trailing boundary then failed and the match was skipped,
    /// so the statement came back unchanged and the pass looked like it was working. The three
    /// spellings are asserted together because they are one rule in `NameWord`.
    /// </remarks>
    [Fact]
    public void A_name_with_an_internal_capital_or_an_apostrophe_is_redacted()
    {
        // A session each, so the placeholder numbers are the first one in both and the assertion is
        // about the name being replaced rather than about the order the session saw them in.
        var internalCapital = Session().Redact("Mr McDonald fitted the replacement.").Text;
        var apostrophe = Session().Redact("I'm Anne-Marie O'Brien and the hinge sticks.").Text;

        Assert.Contains("[NAME_1]", internalCapital, StringComparison.Ordinal);
        Assert.DoesNotContain("McDonald", internalCapital, StringComparison.Ordinal);

        // One person, one placeholder: a pattern that stopped at "Anne-Marie" would leave the
        // surname behind, which is worse than leaving the whole name because it reads as redacted.
        Assert.Contains("[NAME_1]", apostrophe, StringComparison.Ordinal);
        Assert.DoesNotContain("Anne-Marie", apostrophe, StringComparison.Ordinal);
        Assert.DoesNotContain("O'Brien", apostrophe, StringComparison.Ordinal);
        Assert.DoesNotContain("[NAME_2]", apostrophe, StringComparison.Ordinal);

        // Detection reads the same patterns, so the scan sees the identifiers too.
        Assert.True(Session().ContainsIdentifiers("Mr McDonald called about the serial"));
        Assert.True(Session().ContainsIdentifiers("I'm Anne-Marie O'Brien, same fault"));
    }

    /// <summary>
    /// Stability is what lets the model say "the person above": the same name has to keep one
    /// placeholder however the customer arrives at it, and whether or not the caller also supplied it.
    /// </summary>
    [Fact]
    public void One_person_keeps_one_placeholder_across_a_title_and_an_introduction()
    {
        var session = Session();

        var titled = session.Redact("Mr Harrison called about the serial.").Text;
        var introduced = session.Redact("I'm Harrison again, same fault.").Text;
        var supplied = session.Redact("Josephine Smith asked for an update.").Text;

        Assert.Contains("[NAME_1]", titled, StringComparison.Ordinal);
        Assert.Contains("[NAME_1]", introduced, StringComparison.Ordinal);
        Assert.DoesNotContain("[NAME_2]", introduced, StringComparison.Ordinal);
        Assert.Contains("[NAME_2]", supplied, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cost of the pass is measured here: a capitalised product, platform or day after a cue is
    /// not a person, and redacting it would take the fault's own subject away from the model.
    /// </summary>
    [Fact]
    public void A_product_name_is_not_a_person_even_after_a_cue()
    {
        var session = Session();
        const string Platform = "This is Windows 11 and the fan stops after a minute.";
        const string Product = "The Vertex Widget X1 is dead, and the VW-100 mount will not fit.";

        var platform = session.Redact(Platform);
        var product = session.Redact(Product);

        Assert.Equal(Platform, platform.Text);
        Assert.Empty(platform.Counts);

        Assert.Contains("Vertex Widget X1", product.Text, StringComparison.Ordinal);
        Assert.Contains("VW-100", product.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[NAME_", product.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the shorter deny-list costs, recorded here rather than discovered later: a capitalised
    /// ordinary word after a cue is now a person to this pass.
    /// </summary>
    /// <remarks>
    /// The list used to hold the sentence continuations ("Not", "Sorry", "Still", …), the weekdays
    /// and the months, and almost all of them only ever matched a word capitalised after "this is".
    /// The cue stayed and they went, because the cue is what catches "This is Marcus Webb" and a
    /// missed name is the failure this pass exists to prevent, while a masked "Sorry" costs the model
    /// one word of a sentence it can still read. Both halves are asserted so a later reader sees the
    /// trade was chosen: an introduction still redacts, and the continuation is now masked.
    /// </remarks>
    [Fact]
    public void A_capitalised_continuation_after_a_cue_is_masked_rather_than_kept()
    {
        var introduced = Session().Redact("This is Marcus Webb from the repair shop.").Text;
        var continuation = Session().Redact("I'm Sorry for the delay, the unit is still dead.").Text;

        // The half that matters: the cue still finds an introduction.
        Assert.Contains("[NAME_1]", introduced, StringComparison.Ordinal);
        Assert.DoesNotContain("Marcus", introduced, StringComparison.Ordinal);

        // The cost of the trim: "Sorry" names nobody, and nothing in the list says so any more.
        Assert.Contains("[NAME_1]", continuation, StringComparison.Ordinal);
        Assert.DoesNotContain("Sorry", continuation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_serial_stated_in_an_introduction_is_still_kept()
    {
        var redacted = Session()
            .Redact("I'm Priya Raman, serial AX2-0001-0005, and it will not power on.").Text;

        Assert.Contains("AX2-0001-0005", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Priya", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sentence-initial capitals are everywhere in a fault description, and most of them name a part
    /// rather than a person. Without a cue in front of it, a capital is not a name.
    /// </summary>
    [Fact]
    public void A_capitalised_word_that_names_nobody_is_left_alone()
    {
        const string Statement = "The Indicator stays dark. Call Support. Fan Does not spin.";

        var result = Session().Redact(Statement);

        Assert.Equal(Statement, result.Text);
        Assert.Empty(result.Counts);
    }

    /// <summary>
    /// The cue is matched without regard to case and the name after it is not: "I'm still waiting" is
    /// a sentence continuing, and only a capitalised word can be the person who is writing.
    /// </summary>
    [Fact]
    public void An_introduction_cue_in_front_of_an_ordinary_word_names_nobody()
    {
        const string Statement = "This is urgent, and I'm still waiting for a reply.";

        var result = Session().Redact(Statement);

        Assert.Equal(Statement, result.Text);
        Assert.Empty(result.Counts);
    }

    [Fact]
    public void Detection_sees_a_name_that_a_title_or_an_introduction_makes_explicit()
    {
        var session = Session();

        Assert.True(session.ContainsIdentifiers("Dr Ada Lovelace confirmed it"));
        Assert.True(session.ContainsIdentifiers("my name is Marcus Webb"));
        Assert.False(session.ContainsIdentifiers("This is Windows 11 and it fails"));
    }
}
