using System.Security.Cryptography;
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

    /// <summary>The body after sanitisation: HTML, link targets and instruction-like lines removed.</summary>
    public required string Body { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>
    /// Product tokens this article applies to. The single entry "*" means universal.
    /// </summary>
    public required IReadOnlyList<string> ProductScope { get; init; }

    public required IReadOnlyList<string> Keywords { get; init; }

    public required IReadOnlyList<string> Related { get; init; }

    /// <summary>Who last reviewed the article. Required in Production.</summary>
    public string? ReviewedBy { get; init; }

    /// <summary>When the article was last reviewed. Required in Production.</summary>
    public DateTimeOffset? ReviewedAt { get; init; }

    /// <summary>
    /// SHA-256 of the file as loaded. Lets an operator tell "the article is what was reviewed"
    /// from "the article is what someone edited after review".
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>How many instruction-like lines were neutralised while sanitising the body.</summary>
    public int InstructionLikeCount { get; init; }

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
/// Limits applied to the corpus as it is read. Bound from <c>Rag:KnowledgeBase</c>.
/// </summary>
public sealed class KnowledgeBaseOptions
{
    public const string SectionName = "Rag:KnowledgeBase";

    /// <summary>Largest article file accepted, in bytes. A corpus is prose, not a dataset.</summary>
    public long MaxFileBytes { get; set; } = 256 * 1024;

    /// <summary>Largest number of articles the index will hold.</summary>
    public int MaxArticles { get; set; } = 2000;

    /// <summary>Largest front-matter block accepted, in characters.</summary>
    public int MaxFrontMatterCharacters { get; set; } = 4096;

    /// <summary>
    /// When true, an article without <c>reviewed_by</c> and <c>reviewed_at</c> is refused, and its
    /// bytes must match the hash the reviewer recorded in <c>REVIEWED.sha256</c>.
    /// Set by the host in Production, where an unreviewed page is an unreviewed policy answer —
    /// and an edited one is an answer nobody has read.
    /// </summary>
    public bool RequireReview { get; set; }
}

/// <summary>
/// Reads the knowledge-base directory from disk. Fails closed on structural problems:
/// a missing required field or a duplicate id produces an error and the article is not
/// loaded, because a half-loaded corpus with ambiguous ids would make citation
/// verification meaningless.
/// </summary>
/// <remarks>
/// The corpus is written by hand and merged by pull request, which makes it trusted at the
/// process level — but it is still content going into a prompt, and an article can carry
/// instructions as easily as a customer can. Two rules follow. The loader refuses anything it
/// cannot read deterministically (oversize, non-UTF-8, symlinked, unreviewed in Production) and
/// records why, rather than silently dropping it. The body is then sanitised, and the pipeline
/// wraps it in delimiters the system prompt declares as data.
/// </remarks>
public sealed partial class KnowledgeBaseLoader
{
    private const string FrontMatterFence = "---";

    /// <summary>
    /// The checked-in file that records, per article, the hash of the bytes a reviewer read. It
    /// sits at the corpus root and is read only when provenance is required; it is not an article
    /// (the loader reads <c>*.md</c>) and it is reviewed in the same pull request as the articles
    /// it pins.
    /// </summary>
    /// <remarks>
    /// <see cref="KnowledgeArticle.ContentHash"/> is computed while loading, so it can only say
    /// what the file is now. A hash recorded at review time is what says the file is still what
    /// was reviewed: without it, an article edited after approval — a dose halved, a step removed
    /// — loads and is cited exactly like the reviewed text.
    /// </remarks>
    public const string ReviewedHashesFileName = "REVIEWED.sha256";

    /// <summary>Strict UTF-8: a file that is not valid UTF-8 is refused, not repaired.</summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex(@"^---\s*\n(?<frontMatter>[\s\S]*?)\n---\s*\n?(?<body>[\s\S]*)$")]
    private static partial Regex ArticleRegex();

    private readonly ILogger<KnowledgeBaseLoader> _logger;
    private readonly KnowledgeBaseOptions _options;
    private readonly Lock _gate = new();

    public KnowledgeBaseLoader(
        ILogger<KnowledgeBaseLoader> logger,
        Microsoft.Extensions.Options.IOptions<KnowledgeBaseOptions>? options = null)
    {
        _logger = logger;
        _options = options?.Value ?? new KnowledgeBaseOptions();
    }

    /// <summary>
    /// Loads every <c>*.md</c> file under <paramref name="rootPath"/>, recursively.
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

        var rootFull = Path.GetFullPath(rootPath);

        // Read once per load. Null means the manifest is absent or unreadable, and the reason is
        // already recorded: the rest of the corpus is still loaded so /diagnostics can show what
        // else is wrong, and the load result as a whole is not servable.
        var reviewed = _options.RequireReview ? ReadReviewedHashes(rootFull, errors) : null;

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

            if (articles.Count >= _options.MaxArticles)
            {
                // One error, then stop: a per-file error for every remaining file would bury
                // the reason under thousands of copies of it.
                errors.Add(
                    $"Article limit of {_options.MaxArticles} reached; the rest of the corpus under "
                    + $"{rootPath} was not loaded.");
                break;
            }

            if (RefuseStructuralProblem(file, rootFull, errors))
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

                if (reviewed is not null
                    && !TryConfirmReviewedHash(file, rootFull, article, reviewed, errors))
                {
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
    /// Reads the reviewed-hash manifest, in <c>sha256sum</c> format: a 64-character digest, then
    /// whitespace, then the article path relative to the corpus root with forward slashes.
    /// Blank lines and <c>#</c> comments are allowed.
    /// </summary>
    /// <remarks>
    /// Parsed rather than trusted. A truncated paste has to be an error, because the alternative
    /// is comparing a real article against a digest that never belonged to anything. A malformed
    /// line is reported and skipped, which leaves the article it was meant to pin without an
    /// entry, so the article is refused for that reason as well.
    /// </remarks>
    private Dictionary<string, string>? ReadReviewedHashes(string rootFull, List<string> errors)
    {
        var manifestPath = Path.Combine(rootFull, ReviewedHashesFileName);

        if (!File.Exists(manifestPath))
        {
            errors.Add(
                $"{ReviewedHashesFileName} is missing from {rootFull}. Every article needs a "
                + "reviewed hash before the corpus can be served outside Development.");
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Could not read {manifestPath}: {ex.Message}");
            return null;
        }

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // Whitespace-separated into two parts at most, so a path containing spaces survives.
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2
                || parts[0].Length != 64
                || !parts[0].All(Uri.IsHexDigit))
            {
                errors.Add(
                    $"{ReviewedHashesFileName} line {i + 1} is not '<sha256>  <path>': {line}");
                continue;
            }

            hashes[NormalisePath(parts[1])] = parts[0];
        }

        return hashes;
    }

    /// <summary>
    /// Confirms the article still hashes to what the reviewer recorded, refusing it otherwise.
    /// </summary>
    private static bool TryConfirmReviewedHash(
        string file,
        string rootFull,
        KnowledgeArticle article,
        Dictionary<string, string> reviewed,
        List<string> errors)
    {
        var key = NormalisePath(Path.GetRelativePath(rootFull, file));

        if (!reviewed.TryGetValue(key, out var recorded))
        {
            errors.Add(
                $"{file} ({article.Id}) has no line in {ReviewedHashesFileName}. The recorded hash "
                + "is how a reviewed article is told apart from one edited after review; add it in "
                + "the same change as the article.");
            return false;
        }

        if (!string.Equals(recorded, article.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{file} ({article.Id}) changed after review: {ReviewedHashesFileName} records "
                + $"{recorded} but the file hashes to {article.ContentHash}. Re-read the article "
                + "and, if the new text is the reviewed text, update its line.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Manifest paths are written with forward slashes relative to the corpus root, so they mean
    /// the same thing on a Windows checkout and on the Linux host that serves them.
    /// </summary>
    private static string NormalisePath(string path) => path.Trim().Replace('\\', '/');

    /// <summary>
    /// Refuses files the loader cannot vouch for before reading them: symlinks, paths that
    /// resolve outside the root, and anything over the size cap.
    /// </summary>
    private bool RefuseStructuralProblem(string file, string rootFull, List<string> errors)
    {
        var info = new FileInfo(file);

        // A symlink is a path that means something different depending on the machine it was
        // checked out on. Refusing them means the reviewed file is the loaded file.
        if (info.LinkTarget is not null)
        {
            errors.Add($"{file} is a symbolic link. Articles must be regular files in the corpus.");
            _logger.LogError("{File} is a symbolic link and was not loaded.", file);
            return true;
        }

        var full = Path.GetFullPath(file);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            errors.Add($"{file} resolves outside the knowledge-base root {rootFull}.");
            _logger.LogError("{File} resolves outside the knowledge-base root.", file);
            return true;
        }

        if (info.Length > _options.MaxFileBytes)
        {
            errors.Add(
                $"{file} is {info.Length} bytes, over the {_options.MaxFileBytes}-byte limit. "
                + "An article is prose; a file this large is something else.");
            _logger.LogError("{File} is over the size limit.", file);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses one article file. Returns false with a human-readable reason rather than
    /// logging and returning null, so the caller can surface the problem in the load result
    /// and the reload endpoint can refuse to publish a degraded corpus.
    /// </summary>
    private bool TryParseFile(string file, out KnowledgeArticle? article, out string error)
    {
        article = null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not read {file}: {ex.Message}";
            return false;
        }

        string raw;
        try
        {
            raw = StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            error = $"{file} is not valid UTF-8. The corpus is UTF-8 only, so the text cannot be "
                + "indexed or quoted reliably.";
            _logger.LogError("{Error}", error);
            return false;
        }

        var match = ArticleRegex().Match(raw);

        if (!match.Success)
        {
            error = $"Article {file} has no YAML front matter block.";
            _logger.LogError("{Error}", error);
            return false;
        }

        var frontMatterRaw = match.Groups["frontMatter"].Value;
        if (frontMatterRaw.Length > _options.MaxFrontMatterCharacters)
        {
            error = $"{file} has {frontMatterRaw.Length} characters of front matter, over the "
                + $"{_options.MaxFrontMatterCharacters}-character limit.";
            _logger.LogError("{Error}", error);
            return false;
        }

        var frontMatter = ParseFrontMatter(frontMatterRaw);
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

        var reviewedBy = frontMatter.GetValueOrDefault("reviewed_by")?.Trim();
        var reviewedAt = ParseReviewedAt(frontMatter.GetValueOrDefault("reviewed_at"));

        if (_options.RequireReview
            && (string.IsNullOrWhiteSpace(reviewedBy) || reviewedAt is null))
        {
            error = $"Article {file} ({id}) has no provenance. Production refuses an article "
                + "that nobody has reviewed; add reviewed_by and reviewed_at to its front matter.";
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

        // The title is rendered into the prompt as `TITLE: ...`, so it is prompt text exactly as
        // much as the body is, and an instruction placed there would otherwise skip sanitisation
        // entirely. It is cleaned the same way; a title that is nothing but an instruction has no
        // title left, and an article that cannot be named cannot be cited, so it is refused.
        var sanitisedTitle = ArticleSanitizer.Sanitise(title);
        if (sanitisedTitle.Body.Length == 0)
        {
            error = $"Article {file} ({id}) has no title left once sanitised. A title is how a "
                + "citation is recognised; write one that describes the article.";
            _logger.LogError("{Error}", error);
            return false;
        }

        // Keywords are not rendered today, but they are indexed, and a neutralised line kept as
        // a keyword would put the words "instruction-like text removed" into the token set. A
        // keyword that had to be neutralised is dropped instead.
        var sanitisedKeywords = new List<string>();
        var keywordInstructionCount = 0;

        foreach (var keyword in keywords)
        {
            var cleaned = ArticleSanitizer.Sanitise(keyword);
            keywordInstructionCount += cleaned.InstructionLikeCount;

            if (cleaned.InstructionLikeCount == 0 && cleaned.Body.Length > 0)
            {
                sanitisedKeywords.Add(cleaned.Body);
            }
        }

        var sanitised = ArticleSanitizer.Sanitise(body);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in Tokenize(sanitisedTitle.Body))
        {
            tokens.Add(token);
        }

        foreach (var token in Tokenize(sanitised.Body))
        {
            tokens.Add(token);
        }

        foreach (var keyword in sanitisedKeywords)
        {
            foreach (var token in Tokenize(keyword))
            {
                tokens.Add(token);
            }
        }

        var instructionCount = sanitised.InstructionLikeCount
            + sanitisedTitle.InstructionLikeCount
            + keywordInstructionCount;

        article = new KnowledgeArticle
        {
            Id = id,
            Title = sanitisedTitle.Body,
            Category = category,
            Body = sanitised.Body,
            SourcePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), file),
            ProductScope = scope,
            Keywords = sanitisedKeywords,
            Related = related,
            ReviewedBy = string.IsNullOrWhiteSpace(reviewedBy) ? null : reviewedBy,
            ReviewedAt = reviewedAt,
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            InstructionLikeCount = instructionCount,
            Tokens = tokens,
        };

        if (instructionCount > 0)
        {
            // Counted, not quoted: writing the instruction into the log would give it a second
            // life somewhere it is not declared as data.
            _logger.LogWarning(
                "Neutralised {Count} instruction-like line(s) in article {Id}.",
                instructionCount,
                id);
        }

        error = string.Empty;
        return true;
    }

    private static DateTimeOffset? ParseReviewedAt(string? value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

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
