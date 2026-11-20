using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Privacy;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Domain.Triage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIEnabledRma.Tests;

/// <summary>
/// F3: personal data typed by a customer must never reach an AI provider. All values here are
/// synthetic: 555-01xx phone numbers, example.com addresses, published test card numbers.
/// </summary>
public sealed class PiiRedactionTests
{
    // ---------- recognisers ----------

    [Theory]
    [InlineData("mail me at jane.doe+rma@example.com please", "[EMAIL_1]", "jane.doe")]
    [InlineData("card 4111 1111 1111 1111 was charged", "[CARD_1]", "4111")]
    [InlineData("card 5555-5555-5555-4444 was charged", "[CARD_1]", "4444")]
    [InlineData("call +1 (555) 010-4477 after 5", "[PHONE_1]", "4477")]
    [InlineData("call 555.010.9921", "[PHONE_1]", "9921")]
    [InlineData("ship to 2210 Harrison Street, Apt 4B", "[ADDRESS_1]", "Harrison")]
    [InlineData("about order #A-778812", "[ORDER_1]", "778812")]
    [InlineData("ticket number 99812 was closed", "[ORDER_1]", "99812")]
    [InlineData("My name is Dana Whitfield and it broke", "[NAME_1]", "Whitfield")]
    [InlineData("It still fails.\nRegards,\nDana Whitfield", "[NAME_1]", "Whitfield")]
    [InlineData("my other unit ZX9-4410-7731 works", "[ID_1]", "4410")]
    public void Recognisers_replace_planted_values(string text, string placeholder, string mustNotSurvive)
    {
        var redacted = PiiRedactor.Redact(text, new RedactionSession());

        Assert.Contains(placeholder, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(mustNotSurvive, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("The screen stays black although the fan is running.")]
    [InlineData("Firmware 3.2.1 shows error E-104 since 2026-09-01.")]
    [InlineData("I am Unable to boot it after the update.")]
    [InlineData("Restarted it 3 times and held the button for 10 seconds.")]
    public void Ordinary_fault_text_is_left_alone(string text)
    {
        var session = new RedactionSession();

        Assert.Equal(text, PiiRedactor.Redact(text, session));
        Assert.Equal(0, session.Total);
    }

    [Fact]
    public void A_card_number_that_fails_luhn_is_not_called_a_card()
    {
        var redacted = PiiRedactor.Redact("code 4111 1111 1111 1112", new RedactionSession());

        Assert.DoesNotContain("[CARD_", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Protected_device_values_pass_through()
    {
        var session = new RedactionSession(["AX2-0001-0005", "AX-200-HS"]);

        var redacted = PiiRedactor.Redact("AX2-0001-0005 (AX-200-HS) will not charge", session);

        Assert.Equal("AX2-0001-0005 (AX-200-HS) will not charge", redacted);
    }

    [Fact]
    public void Known_names_are_redacted_longest_first_and_case_insensitively()
    {
        var session = new RedactionSession(knownNames: ["Josephine Smyth", "Josephine", "Smyth"]);

        var redacted = PiiRedactor.Redact("josephine smyth here; Smyth family unit", session);

        Assert.DoesNotContain("smyth", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("josephine", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Placeholders_are_stable_within_a_session()
    {
        var session = new RedactionSession();

        var first = PiiRedactor.Redact("email jane@example.com", session);
        var second = PiiRedactor.Redact("again: JANE@example.com, or bob@example.com", session);

        Assert.Equal("email [EMAIL_1]", first);
        Assert.Equal("again: [EMAIL_1], or [EMAIL_2]", second);
        Assert.Equal(2, session.Counts[PiiKind.Email]);
    }

    [Fact]
    public void Detect_ignores_placeholders_and_finds_raw_values()
    {
        var session = new RedactionSession();

        Assert.Empty(PiiRedactor.Detect("We will email [EMAIL_1] about [ORDER_1].", session));
        Assert.Contains(PiiKind.Email, PiiRedactor.Detect("Contact jane@example.com", session));
    }

    // ---------- the gate: nothing planted reaches the model ----------

    /// <summary>Fault statements with planted personal data and the values that must not leak.</summary>
    public static TheoryData<string, string[]> PlantedCorpus => new()
    {
        { "The power light is dark. Email me at dana.w@example.com or call 555-010-3344.", ["dana.w@example.com", "010-3344", "3344"] },
        { "Unit will not turn on. I paid with 4111 1111 1111 1111 on order #B-55120.", ["4111 1111", "55120"] },
        { "Power is dead since Monday. Ship the swap to 41 Elm Street, Unit 7.", ["41 Elm", "Elm Street"] },
        { "My name is Priya Raman and the unit light stays dark.", ["Priya", "Raman"] },
        { "Josephine here, the power light won't turn on. Josephine Smyth", ["Josephine", "Smyth"] },
        { "Dark indicator, will not power. My spare ZX9-4410-7731 works fine.", ["ZX9-4410-7731"] },
        { "light is dark. Thanks, Morgan Ellery", ["Morgan", "Ellery"] },
    };

    [Theory]
    [MemberData(nameof(PlantedCorpus))]
    public async Task No_planted_value_reaches_the_model_or_the_retriever(string statement, string[] planted)
    {
        var (pipeline, model, retriever) = Build();

        await pipeline.TriageAsync(
            new TriageRequest
            {
                Devices = [Device()],
                Conversation = [new TriageTurn { Role = TriageRole.Customer, Content = statement }],
                CustomerStatement = statement,
                KnownPersonalNames = ["Josephine Smyth", "Josephine", "Smyth"],
                CorrelationId = "pii-gate",
            },
            CancellationToken.None);

        var sent = string.Join("\n", model.Requests.SelectMany(r => r.Messages).Select(m => m.Content));
        var queried = string.Join("\n", retriever.Queries.Select(q => q.Query));

        Assert.NotEmpty(model.Requests);
        foreach (var value in planted)
        {
            Assert.DoesNotContain(value, sent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, queried, StringComparison.OrdinalIgnoreCase);
        }

        // The device serial is still there: the policy and the knowledge base need it.
        Assert.Contains("AX2-0001-0005", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_carrying_a_raw_identifier_goes_to_human_review()
    {
        var reply = """
            {
              "resolved": false,
              "confidence": 0.8,
              "summary": "We will call you on 555-010-3344 to arrange the swap.",
              "steps": [ { "instruction": "Check the supply connector.", "article_id": "kb-0001" } ],
              "article_ids": ["kb-0001"]
            }
            """;
        var (pipeline, _, _) = Build(reply);

        var result = await pipeline.TriageAsync(
            new TriageRequest
            {
                Devices = [Device()],
                Conversation = [],
                CustomerStatement = "the power light is dark",
            },
            CancellationToken.None);

        Assert.True(result.UsedFallback);
        Assert.Equal("pii-in-model-output", result.FailureReason);
        Assert.DoesNotContain("3344", result.Verdict.Summary, StringComparison.Ordinal);
    }

    private static TriageDeviceContext Device() => new()
    {
        SerialNumber = "AX2-0001-0005",
        ProductName = "AX-200 Handset",
        Model = "AX-200",
        Sku = "AX-200-HS",
        ProductCategory = "power",
        IsInWarranty = true,
        ScopeTokens = new HashSet<string>(["AX-200 Handset"], StringComparer.OrdinalIgnoreCase),
    };

    private static (TriagePipeline Pipeline, StubChatModel Model, FakeRetriever Retriever) Build(string? reply = null)
    {
        var model = new StubChatModel(new ChatCompletion
        {
            Text = reply ?? """
                {
                  "resolved": false,
                  "confidence": 0.8,
                  "summary": "The supply is not reaching the unit.",
                  "steps": [ { "instruction": "Reseat the supply connector.", "article_id": "kb-0001" } ],
                  "article_ids": ["kb-0001"]
                }
                """,
        });

        var retriever = new FakeRetriever
        {
            Articles =
            [
                new RetrievedArticle { Id = "kb-0001", Title = "Unit will not power on", Category = "power", Score = 0.9, Snippet = "Check the supply." },
            ],
        };

        var pipeline = new TriagePipeline(
            model,
            retriever,
            new ScopeGuard(new ScopeGuardOptions
            {
                TopicVocabularies = [["indicator", "power", "turn", "won't", "dark", "unit", "light", "dead"]],
            }),
            Microsoft.Extensions.Options.Options.Create(new RmaPolicyOptions { HumanReviewConfidenceThreshold = 0.45 }),
            Microsoft.Extensions.Options.Options.Create(new TriagePipelineOptions()),
            NullLogger<TriagePipeline>.Instance);

        return (pipeline, model, retriever);
    }
}
