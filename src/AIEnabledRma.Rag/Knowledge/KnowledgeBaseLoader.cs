using System.Text.RegularExpressions;

namespace AIEnabledRma.Rag.Knowledge;

/// <summary>
/// One knowledge-base article after front matter has been stripped and its body indexed.
/// </summary>
public sealed record KnowledgeArticle
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required string Body { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>
    /// Product tokens this article applies to. The single entry "*" means universal.
    /// </summary>
    public required IReadOnlyList<string> ProductScope { get; init; }

    public required IReadOnlyList<string> Keywords { get; init; }

    public required IReadOnlyList<string> Related { get; init; }

    public bool IsUniversal => ProductScope.Contains("*", StringComparer.Ordinal);

    /// <summary>Lower-cased token set over title, body, and keywords, for lexical scoring.</summary>
    public required IReadOnlySet<string> Tokens { get; init; }
}

public sealed record KnowledgeBaseLoadResult
{
    public IReadOnlyList<KnowledgeArticle> Articles { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public DateTimeOffset LoadedAtUtc { get; init; }

    public string RootPath { get; init; } = string.Empty;
}

/// <summary>
/// Reads the knowledge-base directory from disk. Fails closed on structural problems:
/// a missing required field or a duplicate id produces an error and the article is not
/// loaded, because a half-loaded corpus with ambiguous ids would make citation
/// verification meaningless.
/// </summary>
public sealed partial class KnowledgeBaseLoader
{
    private const string FrontMatterFence = "---";

    [GeneratedRegex(@"^---\s*\n(?<frontMatter>[\s\S]*?)\n---\s*\n?(?<body>[\s\S]*)$")]
    private static partial Regex ArticleRegex();

    private readonly ILogger<KnowledgeBaseLoader> _logger;
    private readonly Lock _gate = new();

    public KnowledgeBaseLoader(ILogger<KnowledgeBaseLoader> logger) => _logger = logger;

    /// <summary>
    /// Loads every <c>*.md</c> file under <paramref name="rootPath"/>, recursively. The
    /// in-memory index is swapped atomically, so a reload never leaves retrieval serving a
    /// half-updated corpus.
    /// </summary>
    public KnowledgeBaseLoadResult Load(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var errors = new List<string>();
        var articles = new List<KnowledgeArticle>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(rootPath))
        {
            errors.Add($"Knowledge-base directory not found: {rootPath}");

            _logger.LogError("Knowledge-base directory not found at {Path}.", rootPath);
            return new KnowledgeBaseLoadResult { Errors = errors, RootPath = rootPath };
        }

        var files = Directory
            .EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            // The directory's own README documents the format; it is not an article.
            if (string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (!TryParseFile(file, out var article, out var error))
                {
                    // The failure reason is recorded, not just logged. A corpus that silently
                    // drops an article is the dangerous case: retrieval keeps working, so
                    // nothing looks broken, but the answer that article would have supported
                    // is quietly missing from a customer-facing reply.
                    errors.Add(error);
                    continue;
                }

                if (!seenIds.Add(article!.Id))
                {
                    errors.Add(
                        $"Duplicate article id '{article.Id}' in {file}. "
                        + "Ids must be unique or citation verification is ambiguous.");
                    continue;
                }

                articles.Add(article);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Could not read {file}: {ex.Message}");
            }
        }

        if (articles.Count == 0 && errors.Count == 0)
        {
            errors.Add($"No articles found under {rootPath}.");
        }

        _logger.LogInformation(
            "Loaded {Count} knowledge-base article(s) from {Path} with {ErrorCount} error(s).",
            articles.Count,
            rootPath,
            errors.Count);

        return new KnowledgeBaseLoadResult
        {
            Articles = articles,
            Errors = errors,
            LoadedAtUtc = DateTimeOffset.UtcNow,
            RootPath = rootPath,
        };
    }

    /// <summary>
    /// Parses one article file. Returns false with a human-readable reason rather than
    /// logging and returning null, so the caller can surface the problem in the load result
    /// and the reload endpoint can refuse to publish a degraded corpus.
    /// </summary>
    private bool TryParseFile(string file, out KnowledgeArticle? article, out string error)
    {
        article = null;

        var raw = File.ReadAllText(file);
        var match = ArticleRegex().Match(raw);

        if (!match.Success)
        {
            error = $"Article {file} has no YAML front matter block.";
            _logger.LogError("{Error}", error);
            return false;
        }

        var frontMatter = ParseFrontMatter(match.Groups["frontMatter"].Value);
        var body = match.Groups["body"].Value.Trim();

        var id = frontMatter.GetValueOrDefault("id")?.Trim();
        var title = frontMatter.GetValueOrDefault("title")?.Trim();
        var category = frontMatter.GetValueOrDefault("category")?.Trim();

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)
            || string.IsNullOrWhiteSpace(category))
        {
            var missing = new[] { ("id", id), ("title", title), ("category", category) }
                .Where(f => string.IsNullOrWhiteSpace(f.Item2))
                .Select(f => f.Item1);

            error = $"Article {file} is missing required front matter: {string.Join(", ", missing)}.";
            _logger.LogError("{Error}", error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            error = $"Article {file} ({id}) has an empty body. It would be retrievable but "
                + "useless, and would consume a slot in the top-K.";
            _logger.LogError("{Error}", error);
            return false;
        }

        var scope = ParseList(frontMatter.GetValueOrDefault("product_scope"));
        if (scope.Count == 0)
        {
            // Absent scope would make the article silently invisible to every session, which
            // is a confusing failure. Defaulting to universal instead means a missing field
            // over-shares rather than silently disabling an article.
            scope = ["*"];
        }

        var keywords = ParseList(frontMatter.GetValueOrDefault("keywords"));
        var related = ParseList(frontMatter.GetValueOrDefault("related"));

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokenize(title))
        {
            tokens.Add(token);
        }

        foreach (var token in Tokenize(body))
        {
            tokens.Add(token);
        }

        foreach (var keyword in keywords)
        {
            foreach (var token in Tokenize(keyword))
            {
                tokens.Add(token);
            }
        }

        article = new KnowledgeArticle
        {
            Id = id,
            Title = title,
            Category = category,
            Body = body,
            SourcePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), file),
            ProductScope = scope,
            Keywords = keywords,
            Related = related,
            Tokens = tokens,
        };

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Minimal YAML subset parser: <c>key: value</c> lines plus inline <c>[a, b]</c> lists.
    /// Deliberately not a full YAML implementation, so the corpus has no third-party parser
    /// dependency and no YAML feature that behaves ambiguously across implementations.
    /// </summary>
    internal static Dictionary<string, string> ParseFrontMatter(string yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r').Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Strips one layer of matching surrounding quotes from a scalar value.
    /// YAML authors quote titles and ids out of habit, and without this the quotes survive
    /// into the index: a customer-visible title keeps its quote characters, and an id
    /// written <c>id: "kb-0001"</c> stops matching the <c>kb-0001</c> referenced from a
    /// sibling article's <c>related</c> list. List values are unquoted by
    /// <see cref="ParseList"/> instead, which is why it needs its own trimming.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    internal static List<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().Trim('"', '\''))
            .Where(t => t.Length > 0)
            .ToList();
    }

    private static IEnumerable<string> Tokenize(string value) =>
        Regex.Matches(value.ToLowerInvariant(), "[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 2);
}
