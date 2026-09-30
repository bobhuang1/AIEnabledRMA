using System.Text.RegularExpressions;
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
public sealed partial class ScopeGuard(ScopeGuardOptions options) : IScopeGuard
{
    private readonly ScopeGuardOptions _options = options;

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

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

        // Check 3: topic relevance. A minimum share of the question's tokens must land in
        // the allowed topic vocabulary, which keeps the guard from becoming a keyword
        // allowlist that a determined user could learn.
        var tokens = TokenRegex().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 2)
            .ToArray();

        if (tokens.Length == 0)
        {
            return OutOfScope("no-meaningful-tokens");
        }

        var topicHits = tokens.Count(t =>
            _options.TopicVocabularies.Any(v => v.Contains(t, StringComparer.OrdinalIgnoreCase)));

        var topicRatio = (double)topicHits / tokens.Length;
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

    /// <summary>Vocabularies that count as on-topic. Any one matching token is a hit.</summary>
    public List<List<string>> TopicVocabularies { get; set; } = [];

    /// <summary>Phrases that immediately mark the request as out of domain.</summary>
    public List<string> DenyPhrases { get; set; } = [];

    public string RefusalMessage { get; set; } =
        "I can only help with troubleshooting the item you are returning. "
        + "If you need help with anything else, please contact our support team.";
}
