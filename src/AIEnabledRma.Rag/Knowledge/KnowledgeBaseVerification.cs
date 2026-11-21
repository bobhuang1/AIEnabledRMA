using System.Security.Cryptography;

namespace AIEnabledRma.Rag.Knowledge;

/// <summary>
/// What the corpus on disk looks like next to the manifest the reviewers committed.
/// </summary>
public sealed record KnowledgeBaseVerification
{
    /// <summary>Article files found under the corpus root, excluding the directory's README.</summary>
    public required int Articles { get; init; }

    /// <summary>Well-formed lines in <see cref="KnowledgeBaseLoader.ReviewedHashesFileName"/>.</summary>
    public required int ManifestEntries { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool Matches => Problems.Count == 0;
}

/// <summary>
/// Checks the loaded corpus against <see cref="KnowledgeBaseLoader.ReviewedHashesFileName"/> directly,
/// rather than only when the loader happens to require review.
/// </summary>
/// <remarks>
/// The manifest used to matter only in Production, where the loader compares each article's hash
/// against it. Everywhere else — a developer's machine, a container started with
/// <c>DOTNET_ENVIRONMENT=Development</c>, any host whose corpus path points somewhere unexpected —
/// a missing manifest, a missing article or an article edited after review loaded and answered
/// without a word. The two failures this repository has already had were exactly that shape: an
/// image with no corpus, and a publish that carried no manifest. Both were silent from the host's
/// side, which is why the check is a startup obligation and not a review-time one.
/// </remarks>
public static class KnowledgeBaseVerifier
{
    public static KnowledgeBaseVerification Verify(KnowledgeBaseLoadResult loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        var problems = new List<string>();
        var root = loaded.RootPath;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            problems.Add($"the corpus directory does not exist: '{root}'");

            return new KnowledgeBaseVerification { Articles = loaded.Articles.Count, ManifestEntries = 0, Problems = problems };
        }

        var files = ArticleFiles(root);

        // The loader records why it refused a file, so a gap between what is on disk and what it
        // indexed is not automatically a manifest problem; reporting it names the difference.
        if (files.Count != loaded.Articles.Count)
        {
            problems.Add(
                $"the loader indexed {loaded.Articles.Count} of {files.Count} article file(s) under {root}");
        }

        var manifestPath = Path.Combine(root, KnowledgeBaseLoader.ReviewedHashesFileName);

        if (!File.Exists(manifestPath))
        {
            problems.Add(
                $"{KnowledgeBaseLoader.ReviewedHashesFileName} is missing from {root}; without it nothing "
                + "says the articles are the ones that were reviewed");

            return new KnowledgeBaseVerification { Articles = files.Count, ManifestEntries = 0, Problems = problems };
        }

        var manifest = ReadManifest(manifestPath, problems);
        var manifestKeys = new HashSet<string>(manifest.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relative = NormalisePath(Path.GetRelativePath(root, file));

            if (!manifest.TryGetValue(relative, out var recorded))
            {
                problems.Add($"{relative} has no line in {KnowledgeBaseLoader.ReviewedHashesFileName}");
                continue;
            }

            var actual = Hash(file, problems);

            if (actual is null)
            {
                continue;
            }

            if (!string.Equals(recorded, actual, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{relative} changed after review: the manifest records {recorded} but the file hashes to {actual}");
            }

            manifestKeys.Remove(relative);
        }

        foreach (var orphan in manifestKeys.OrderBy(key => key, StringComparer.Ordinal))
        {
            problems.Add(
                $"{KnowledgeBaseLoader.ReviewedHashesFileName} lists {orphan}, which is not a file under {root}");
        }

        return new KnowledgeBaseVerification
        {
            Articles = files.Count,
            ManifestEntries = manifest.Count,
            Problems = problems,
        };
    }

    private static List<string> ArticleFiles(string root) =>
        Directory
            .EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, string> ReadManifest(string path, List<string> problems)
    {
        var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"could not read {path}: {ex.Message}");

            return manifest;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit))
            {
                problems.Add($"{KnowledgeBaseLoader.ReviewedHashesFileName} line {i + 1} is not '<sha256>  <path>': {line}");
                continue;
            }

            manifest[NormalisePath(parts[1])] = parts[0];
        }

        return manifest;
    }

    private static string? Hash(string file, List<string> problems)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"could not read {file}: {ex.Message}");

            return null;
        }
    }

    private static string NormalisePath(string path) => path.Trim().Replace('\\', '/');
}
