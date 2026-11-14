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
}
