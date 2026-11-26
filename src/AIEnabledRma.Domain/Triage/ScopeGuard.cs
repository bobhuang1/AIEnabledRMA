using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Triage;

namespace AIEnabledRma.Domain.Triage;

/// <summary>
/// Deterministic scope guard. Runs before the model is called and decides whether the
/// question is answerable at all. A question that does not match the devices in the current
/// session never reaches the model, so there is no output to guard.
/// </summary>
public interface IScopeGuard
{
    ScopeGuardResult Evaluate(TriageRequest request, IReadOnlyList<RetrievedArticle> candidates);
}

public sealed record ScopeGuardResult
{
    /// <summary>True when the question is on-topic and may be answered from the corpus.</summary>
    public required bool InScope { get; init; }

    /// <summary>Canned customer-facing refusal, present when <see cref="InScope"/> is false.</summary>
    public string? RefusalMessage { get; init; }

    /// <summary>Why the guard decided as it did, for the audit log.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Keyword-and-token scope guard. Two independent checks, both of which must pass:
/// a topic check (does the text relate to hardware, the catalog, or the RMA process) and a
/// scope-token check (does it mention something the session's devices actually cover).
/// </summary>
/// <remarks>
/// Words are compared by their stems, the reduction <c>TextTokens</c> already applies to the
/// corpus and to the question. Comparing them as written is what refused "the unit reboots by itself
/// every few minutes" — <c>kb-0014</c>'s own symptom — while the article's keyword is "reboot", so the
/// guard now asks whether the word belongs to the domain rather than which spelling of it was typed.
/// The ratio is taken over the question's topic words only: function words are dropped from both sides
/// rather than counted as evidence for or against the question being in scope.
/// </remarks>
public sealed class ScopeGuard : IScopeGuard
{
    private readonly ScopeGuardOptions _options;

    /// <summary>
    /// Every word the corpus declared, reduced to a stem, because a word is matched by what it means
    /// and not by how it was inflected at either end. The vocabularies stay separate on the way in —
    /// one per category — but a question may mix a display fault with a warranty question, so which
    /// category contributed a word does not change the answer.
    /// </summary>
    private readonly HashSet<string> _topicStems;

    /// <summary>
    /// The topic vocabulary is a construction argument rather than an option, so the words the
    /// guard recognises are supplied where they are known — from the knowledge base — and a
    /// guard that recognises nothing cannot be built at all.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// No vocabulary contains a word, which would make every question off-topic.
    /// </exception>
    public ScopeGuard(
        ScopeGuardOptions options,
        IReadOnlyList<IReadOnlyList<string>> topicVocabulary)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(topicVocabulary);

        // Every entry goes through the same reduction the question will: a caller that hands over a
        // phrase, a plural or a word list written in another inflection gets the same answer as one
        // that copy-pasted the corpus's keyword.
        _topicStems = topicVocabulary
            .SelectMany(vocabulary => vocabulary)
            .SelectMany(ScopeGuardVocabulary.Tokens)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // This used to be an option that could be empty, and the symptom was indistinguishable
        // from a broken assistant: every question refused, nothing in the log to explain it. A
        // vocabulary of nothing but function words has the same symptom, so it is the same refusal.
        if (_topicStems.Count == 0)
        {
            throw new ArgumentException(
                "The scope guard needs at least one topic word; with an empty vocabulary it "
                + "refuses every question as off-topic.",
                nameof(topicVocabulary));
        }

        _options = options;
    }

    public ScopeGuardResult Evaluate(
        TriageRequest request,
        IReadOnlyList<RetrievedArticle> candidates)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Nothing in the session means nothing to scope against.
        if (request.Devices.Count == 0)
        {
            return OutOfScope("no-devices-in-session");
        }

        var text = BuildText(request);
        if (string.IsNullOrWhiteSpace(text))
        {
            return OutOfScope("empty-input");
        }

        // Check 1: the corpus must have at least one article in the session's product family.
        // This is the strongest signal and is evaluated before the keyword heuristics, so a
        // short question like "no" still gets the right refusal.
        if (candidates.Count == 0)
        {
            return OutOfScope("no-scoped-knowledge");
        }

        // Check 2: explicit out-of-domain intent.
        if (_options.DenyPhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return OutOfScope("out-of-domain-phrase");
        }

        // Check 3: topic relevance. A minimum share of the question's topic words must land in
        // the allowed vocabulary, which keeps the guard from becoming a keyword allowlist that a
        // determined user could learn. The question is reduced exactly as the vocabulary was, so
        // "it reboots" reaches an article whose keyword is "reboot" and "rebooting" reaches both.
        var tokens = ScopeGuardVocabulary.Tokens(text);

        if (tokens.Count == 0)
        {
            return OutOfScope("no-meaningful-tokens");
        }

        var topicHits = tokens.Count(_topicStems.Contains);

        var topicRatio = (double)topicHits / tokens.Count;
        if (topicRatio < _options.MinimumTopicTokenRatio)
        {
            return OutOfScope("off-topic");
        }

        return new ScopeGuardResult { InScope = true, Reason = "in-scope" };
    }

    private string BuildText(TriageRequest request)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.CustomerStatement))
        {
            parts.Add(request.CustomerStatement);
        }

        // Only recent customer turns feed the topic check. Including assistant text would let
        // the assistant's own vocabulary inflate the ratio.
        parts.AddRange(request.Conversation
            .Where(t => t.Role == TriageRole.Customer)
            .TakeLast(_options.CustomerTurnsToConsider)
            .Select(t => t.Content));

        return string.Join(' ', parts);
    }

    private ScopeGuardResult OutOfScope(string reason) => new()
    {
        InScope = false,
        RefusalMessage = _options.RefusalMessage,
        Reason = reason,
    };
}

public sealed class ScopeGuardOptions
{
    public const string SectionName = "Triage:ScopeGuard";

    /// <summary>Share of question tokens that must be on-topic, 0..1.</summary>
    public double MinimumTopicTokenRatio { get; set; } = 0.2;

    /// <summary>How many of the most recent customer turns feed the topic check.</summary>
    public int CustomerTurnsToConsider { get; set; } = 3;

    /// <summary>Phrases that immediately mark the request as out of domain.</summary>
    public List<string> DenyPhrases { get; set; } = [];

    public string RefusalMessage { get; set; } =
        "I can only help with troubleshooting the item you are returning. "
        + "If you need help with anything else, please contact our support team.";
}
