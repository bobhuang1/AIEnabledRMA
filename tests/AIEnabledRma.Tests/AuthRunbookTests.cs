using System.Text.RegularExpressions;

namespace AIEnabledRma.Tests;

/// <summary>
/// The auth gate is one script, and both the runbook and CI point at it.
/// </summary>
/// <remarks>
/// The flow this guards was verified by hand once, and a hand-run flow is the kind of documentation
/// that rots: the realm changes, the runbook keeps its recipe, and nothing notices until someone
/// follows it. The script is the recipe, CI runs it, and <c>docs/mcp.md</c> tells the reader to run
/// it. This test keeps those three references identical, so renaming the script fails here instead
/// of leaving a runbook that describes a file nobody has.
/// </remarks>
public sealed class AuthRunbookTests
{
    private const string ScriptPath = "scripts/verify-auth.sh";

    [Fact]
    public void The_runbook_and_ci_name_the_same_script()
    {
        var root = RepositoryRoot();

        var script = Path.Combine(root, ScriptPath);

        Assert.True(File.Exists(script), $"the auth check is missing at {ScriptPath}");

        // The runbook's "Verify it in one command" section must run the same file CI runs, not a
        // re-typed recipe beside it.
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "mcp.md"));

        Assert.Contains("./" + ScriptPath, runbook, StringComparison.Ordinal);

        // The path has to appear in a command block, so a passing mention in prose cannot stand in
        // for an instruction the reader can copy.
        Assert.Matches(
            new Regex($@"```bash\r?\n[^`]*\./{Regex.Escape(ScriptPath)}", RegexOptions.CultureInvariant),
            runbook);

        // CI invokes it as a step, which is what makes the runbook's claim about it true rather than
        // aspirational.
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains("run: ./" + ScriptPath, workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from the test binary to the solution directory. The RAG project copies the corpus
    /// next to the binary, so stopping at the first directory with a knowledge-base would look at a
    /// build artifact instead of the checkout.
    /// </summary>
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
}
