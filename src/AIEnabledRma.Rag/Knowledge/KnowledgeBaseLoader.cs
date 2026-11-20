using System.Text;
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

    /// <summary>Who reviewed this article (front matter <c>reviewed_by</c>), if anyone.</summary>
    public string? ReviewedBy { get; init; }

    /// <summary>When it was reviewed (front matter <c>reviewed_at</c>).</summary>
    public DateOnly? ReviewedAt { get; init; }

    /// <summary>SHA-256 of the body as written, line endings normalised.</summary>
    public string ContentHash { get; init; } = string.Empty;

    public bool IsReviewed => !string.IsNullOrWhiteSpace(ReviewedBy) && ReviewedAt is not null;

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
    public KnowledgeBaseLoadResult Load(string rootPath, KnowledgeIngestionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        options ??= new KnowledgeIngestionOptions();

        var errors = new List<string>();
        var articles = new List<KnowledgeArticle>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(rootPath))
        {
            errors.Add($"Knowledge-base directory not found: {rootPath}");

            _logger.LogError("Knowledge-base directory not found at {Path}.", rootPath);
            return new KnowledgeBaseLoadResult { Errors = errors, RootPath = rootPath };
        }

        var fullRoot = Path.GetFullPath(rootPath);

        // Links are not followed: a symlinked directory or file could pull in text from anywhere
        // on the machine, outside the reviewed tree.
        var files = Directory
            .EnumerateFiles(rootPath, "*.md", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
            })
            .Where(f => !string.Equals(Path.GetFileName(f), "README.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (files.Count > options.MaxArticles)
        {
            errors.Add(
                $"The knowledge base has {files.Count} article files, more than the limit of "
                + $"{options.MaxArticles}. Nothing beyond the limit is read.");
            files = files.Take(options.MaxArticles).ToList();
        }

        foreach (var file in files)
        {
            try
            {
                if (LinkProblem(file, fullRoot) is { } linkError)
                {
                    errors.Add(linkError);
                    continue;
                }

                if (!TryParseFile(file, options, out var article, out var error))
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
    private bool TryParseFile(
        string file,
        KnowledgeIngestionOptions options,
        out KnowledgeArticle? article,
        out string error)
    {
        article = null;

        var length = new FileInfo(file).Length;
        if (length > options.MaxFileBytes)
        {
            error = $"Article {file} is {length} bytes, over the {options.MaxFileBytes}-byte limit.";
            _logger.LogError("{Error}", error);
            return false;
        }

        string raw;
        try
        {
            raw = StrictUtf8.GetString(File.ReadAllBytes(file)).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            error = $"Article {file} is not valid UTF-8.";
            _logger.LogError("{Error}", error);
            return false;
        }

        raw = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        var match = ArticleRegex().Match(raw);

        if (!match.Success)
        {
            error = $"Article {file} has no YAML front matter block.";
            _logger.LogError("{Error}", error);
            return false;
        }

        var frontMatterText = match.Groups["frontMatter"].Value;
        if (Encoding.UTF8.GetByteCount(frontMatterText) > options.MaxFrontMatterBytes)
        {
            error = $"Article {file} has front matter over the {options.MaxFrontMatterBytes}-byte limit.";
            _logger.LogError("{Error}", error);
            return false;
        }

        var frontMatter = ParseFrontMatter(frontMatterText);
        var rawBody = match.Groups["body"].Value.Trim();

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

        // Provenance. The hash covers the body as written; when the front matter records one,
        // an edit made after review no longer matches and the article is refused.
        var contentHash = KnowledgeSanitizer.ContentHash(rawBody);
        var recordedHash = frontMatter.GetValueOrDefault("content_hash")?.Trim();
        if (!string.IsNullOrEmpty(recordedHash)
            && !string.Equals(recordedHash, contentHash, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Article {file} ({id}) was changed after review: its content_hash does not match the body.";
            _logger.LogError("{Error}", error);
            return false;
        }

        var reviewedBy = frontMatter.GetValueOrDefault("reviewed_by")?.Trim();
        DateOnly? reviewedAt = DateOnly.TryParseExact(
            frontMatter.GetValueOrDefault("reviewed_at")?.Trim(),
            "yyyy-MM-dd",
            out var parsedReviewedAt)
            ? parsedReviewedAt
            : null;

        if (options.RequireReviewed && (string.IsNullOrWhiteSpace(reviewedBy) || reviewedAt is null))
        {
            error = $"Article {file} ({id}) has not been reviewed (reviewed_by and reviewed_at are required).";
            _logger.LogError("{Error}", error);
            return false;
        }

        // An article that talks to the model is refused outright. It is reference data; a
        // reviewed article has no reason to contain instructions.
        if (KnowledgeSanitizer.FindInstructionLikeText(title + "\n" + rawBody) is { } injected)
        {
            error = $"Article {file} ({id}) contains instruction-like text and was refused: \"{Truncate(injected, 80)}\".";
            _logger.LogError("{Error}", error);
            return false;
        }

        var body = KnowledgeSanitizer.Sanitize(rawBody).Trim();
        title = KnowledgeSanitizer.NeutraliseDelimiters(title);

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
            ReviewedBy = string.IsNullOrWhiteSpace(reviewedBy) ? null : reviewedBy,
            ReviewedAt = reviewedAt,
            ContentHash = contentHash,
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

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Refuses a file that is a link, or that sits under a linked directory, or that resolves
    /// outside the root.
    /// </summary>
    private static string? LinkProblem(string file, string fullRoot)
    {
        var info = new FileInfo(file);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return $"Article {file} is a symbolic link and was not read.";
        }

        var fullPath = Path.GetFullPath(file);
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return $"Article {file} resolves outside the knowledge-base root and was not read.";
        }

        for (var dir = info.Directory; dir is not null && dir.FullName.Length > fullRoot.TrimEnd(Path.DirectorySeparatorChar).Length; dir = dir.Parent)
        {
            if (dir.LinkTarget is not null || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return $"Article {file} is under a linked directory ({dir.FullName}) and was not read.";
            }
        }

        return null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "...";

    private static IEnumerable<string> Tokenize(string value) =>
        Regex.Matches(value.ToLowerInvariant(), "[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 2);
}
