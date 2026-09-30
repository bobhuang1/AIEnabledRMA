using System.Text.Json;
using System.Text.RegularExpressions;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Triage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Ai;

/// <summary>
/// The bundled example AI. No network calls, no API key, no model download, fully
/// deterministic. It is the default provider so that a fresh clone runs the entire
/// troubleshooting flow offline, and so the test suite has a stable fixture to assert
/// against.
///
/// What it does: it re-reads the retrieved knowledge-base articles that the RAG service
/// put in the prompt, picks the ones whose keywords overlap the customer's description, and
/// composes a verdict that quotes those articles. It is a retrieval-follower, not a
/// reasoner, which is exactly the behaviour the lockdown wants.
///
/// What it is not: a demonstration of what a real model would produce. Swap in your own
/// provider for that. See <see cref="README"/> at the repository root for the recipe.
/// </summary>
public sealed partial class ExampleLocalChatModel(
    IOptions<ExampleModelOptions> options,
    ILogger<ExampleLocalChatModel> logger) : IChatModel
{
    private readonly ExampleModelOptions _options = options.Value;

    [GeneratedRegex(@"<article\s+id=""(?<id>[^""]+)""[^>]*>(?<body>[\s\S]*?)</article>",
        RegexOptions.IgnoreCase)]
    private static partial Regex ArticleBlockRegex();

    [GeneratedRegex(@"^TITLE:\s*(?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex TokenRegex();

    public string ProviderName => "example-local";

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var articles = ParseArticles(request);
        var question = ExtractLastUserText(request);

        var (verdict, explanation) = BuildVerdict(articles, question);

        logger.LogDebug(
            "Example model composed a verdict from {Count} article(s): {Verdict}",
            articles.Count,
            verdict.Resolved);

        var json = JsonSerializer.Serialize(verdict, new JsonSerializerOptions
        {
            WriteIndented = false,
        });

        return Task.FromResult(new ChatCompletion
        {
            Text = json,
            FinishReason = "stop",
            PromptTokens = request.Messages.Sum(m => TokenRegex().Matches(m.Content).Count),
            CompletionTokens = TokenRegex().Matches(json).Count,
        });
    }

    /// <summary>
    /// The customer's most recent message. The final user message is the current turn; the
    /// ones before it are earlier turns replayed as history.
    /// </summary>
    private static string ExtractLastUserText(ChatRequest request) =>
        request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;

    /// <summary>
    /// Rebuilds the retrieval set from the prompt. A real model reads this text; the example
    /// parses it, which is what keeps it honest — it can only cite what was given to it, and
    /// the pipeline verifies those citations independently regardless.
    /// </summary>
    private static List<ParsedArticle> ParseArticles(ChatRequest request)
    {
        var list = new List<ParsedArticle>();

        foreach (var message in request.Messages)
        {
            if (!message.Content.Contains("<knowledge>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match match in ArticleBlockRegex().Matches(message.Content))
            {
                var body = match.Groups["body"].Value;
                var titleMatch = TitleRegex().Match(body);

                list.Add(new ParsedArticle(
                    Id: match.Groups["id"].Value.Trim(),
                    Title: titleMatch.Success ? titleMatch.Groups["title"].Value.Trim() : string.Empty,
                    Text: body));
            }
        }

        return list;
    }

    private (TriageVerdict Verdict, string Explanation) BuildVerdict(
        List<ParsedArticle> articles,
        string question)
    {
        if (articles.Count == 0)
        {
            return (
                new TriageVerdict
                {
                    Resolved = false,
                    Confidence = 0d,
                    Summary = "I do not have any troubleshooting material for this item yet. "
                              + "Please contact support so a specialist can help.",
                    Steps = [],
                    ArticleIds = [],
                },
                "no articles in context");
        }

        var questionTokens = TokenRegex()
            .Matches(question.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 2)
            .ToHashSet();

        // Rank by keyword overlap with the customer's own words. Ties break on the order the
        // RAG service supplied, which is already relevance-ordered.
        var ranked = articles
            .Select((article, index) => new
            {
                Article = article,
                Index = index,
                Overlap = Overlap(questionTokens, article),
            })
            .OrderByDescending(x => x.Overlap)
            .ThenBy(x => x.Index)
            .ToList();

        var best = ranked[0];
        var resolved = DetectResolution(question);

        var steps = ranked
            .Where(x => x.Overlap > 0)
            .Take(_options.MaxSteps)
            .Select(x => new TriageStep
            {
                Instruction = SummariseStep(x.Article),
                ArticleId = x.Article.Id,
            })
            .ToArray();

        var confidence = resolved
            ? Math.Clamp(0.75d + (best.Overlap / 20d), 0d, 0.95d)
            : Math.Clamp(0.5d + (best.Overlap / 12d), 0d, _options.MaxUnresolvedConfidence);

        var category = GuessCategory(ranked.Select(r => r.Article.Text).FirstOrDefault() ?? string.Empty);

        var summary = resolved
            ? "You have confirmed the fault is resolved, so no return is needed. "
              + "Thank you for trying those steps."
            : BuildSummary(best.Article, ranked.Count, steps.Length);

        return (
            new TriageVerdict
            {
                Resolved = resolved,
                Confidence = confidence,
                Summary = summary,
                ProblemCategoryCode = category,
                Steps = steps,
                ArticleIds = steps.Select(s => s.ArticleId!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            },
            $"ranked {articles.Count} article(s), best overlap {best.Overlap}, resolved={resolved}");
    }

    private static int Overlap(HashSet<string> questionTokens, ParsedArticle article)
    {
        if (questionTokens.Count == 0)
        {
            return 0;
        }

        var articleTokens = TokenRegex()
            .Matches(article.Text.ToLowerInvariant())
            .Select(m => m.Value)
            .ToHashSet();

        return questionTokens.Count(articleTokens.Contains);
    }

    /// <summary>
    /// Resolution is only ever taken from an explicit customer statement, never inferred. This
    /// mirrors the rule the system prompt gives a real model, and the pipeline downgrades a
    /// resolved verdict that carries no citable step regardless of what a provider returns.
    /// </summary>
    private bool DetectResolution(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return false;
        }

        return _options.ResolutionPhrases.Any(p =>
            question.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string SummariseStep(ParsedArticle article)
    {
        // Take the first bullet or numbered item from the article as the actionable step.
        var lines = article.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var candidate = lines.FirstOrDefault(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith("- ") || char.IsAsciiDigit(t.FirstOrDefault('0'));
        });

        return (candidate ?? lines.FirstOrDefault() ?? article.Title).Trim().TrimStart('-', ' ');
    }

    private static string BuildSummary(ParsedArticle article, int candidateCount, int stepCount)
    {
        var topic = string.IsNullOrWhiteSpace(article.Title) ? "this issue" : article.Title.ToLowerInvariant();

        return stepCount == 0
            ? $"I found guidance about {topic}. Please tell me what happens after each step, "
              + "and I will help you work through it."
            : $"Based on {candidateCount} matching article(s), please work through the {stepCount} "
              + $"step(s) below for {topic}. "
              + "Let me know the result of each one so I can tell you whether the fault is real.";
    }

    private static string? GuessCategory(string articleText)
    {
        foreach (var (token, category) in _categoryMap)
        {
            if (articleText.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return null;
    }

    private static readonly (string Token, string Category)[] _categoryMap =
    [
        ("charge", "power"),
        ("power on", "power"),
        ("battery", "power"),
        ("screen", "display"),
        ("display", "display"),
        ("bluetooth", "connectivity"),
        ("pair", "connectivity"),
        ("radio", "connectivity"),
        ("trigger", "scanning"),
        ("barcode", "scanning"),
        ("decode", "scanning"),
        ("scan window", "scanning"),
        ("dropped", "physical"),
        ("impact", "physical"),
        ("liquid", "physical"),
        ("water", "physical"),
        ("firmware", "software"),
        ("reboot", "software"),
        ("freeze", "software"),
        ("cable", "merchandise"),
        ("adapter", "merchandise"),
    ];

    private sealed record ParsedArticle(string Id, string Title, string Text);
}

public sealed class ExampleModelOptions
{
    public const string SectionName = "Ai:ExampleLocal";

    public int MaxSteps { get; set; } = 4;

    /// <summary>
    /// Cap on the confidence the example reports for an unresolved fault. Deliberately below
    /// the default auto-approve threshold so the demo does not accidentally imply that
    /// unresolved triage is good enough to auto-approve a return.
    /// </summary>
    public double MaxUnresolvedConfidence { get; set; } = 0.6;

    /// <summary>
    /// Phrases that constitute an explicit statement that the fault is fixed. Matched
    /// case-insensitively as substrings of the customer's message.
    /// </summary>
    public List<string> ResolutionPhrases { get; set; } =
    [
        "it works now",
        "that fixed it",
        "fixed it",
        "working now",
        "back to normal",
        "seems to be working",
        "no longer have the problem",
        "issue is gone",
    ];
}
