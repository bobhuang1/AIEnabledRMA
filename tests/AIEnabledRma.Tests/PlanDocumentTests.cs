using System.Text.RegularExpressions;

namespace AIEnabledRma.Tests;

/// <summary>
/// The decision index at the top of plan.md is the entry point to the document, so a pointer in it
/// that no longer resolves sends a reader nowhere and is invisible to anyone reading the plan in
/// order.
/// </summary>
/// <remarks>
/// plan.md is one document holding both the decisions and the per-workstream status lines; the index
/// is the only place that lists them together. This asserts the "Where" column, not the prose: a row
/// may name a type or a test as well as a file, and only the paths are checkable.
/// </remarks>
public sealed class PlanDocumentTests
{
    private static readonly string[] CheckableSuffixes = [".md", ".yml", ".json", ".cs", ".sh"];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !(Directory.Exists(Path.Combine(directory.FullName, "knowledge-base"))
                    && File.Exists(Path.Combine(directory.FullName, "AIEnabledRma.slnx"))))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static bool Exists(string root, string pointer)
    {
        var path = Path.Combine(
            root,
            pointer.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));

        return File.Exists(path) || Directory.Exists(path);
    }

    [Fact]
    public void Every_file_the_decision_index_points_at_exists()
    {
        var root = RepositoryRoot();
        var plan = File.ReadAllText(Path.Combine(root, "plan.md"));

        var start = plan.IndexOf("## 0. Decision index", StringComparison.Ordinal);
        var end = start < 0 ? -1 : plan.IndexOf("\n## 1.", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "plan.md no longer has a decision index section");

        var rows = plan[start..end]
            .Split('\n')
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
            .ToList();

        // The header row and the separator are two of these; eleven decisions is the low-water mark
        // that still means the table has not been emptied out from under the index's introduction.
        Assert.True(rows.Count >= 11, $"the decision index has {rows.Count} rows, which is fewer than its decisions");

        var pointers = rows
            .SelectMany(row => Regex.Matches(row, "`([^`]+)`").Select(match => match.Groups[1].Value))
            .Where(pointer => pointer.Contains('/')
                              || CheckableSuffixes.Any(suffix => pointer.EndsWith(suffix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(pointers);

        // A pointer may name a directory (`knowledge-base/`), so existence is either kind of entry.
        var missing = pointers
            .Where(pointer => !Exists(root, pointer))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "plan.md's decision index points at files that do not exist: " + string.Join(", ", missing));
    }
}
