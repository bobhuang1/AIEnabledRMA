using System.Text.RegularExpressions;

namespace AIEnabledRma.Tests;

/// <summary>
/// The workflow has to run on the branch that ships, and the two expensive jobs have to stay jobs.
/// </summary>
/// <remarks>
/// This repository spent a stretch with a CI file that triggered only on pull requests and manual
/// dispatches. Twenty commits were pushed to main and validated by nothing: the last recorded run was
/// a failure from days earlier. The trigger is a one-line property with a large consequence, so it is
/// asserted here rather than trusted to survive the next edit of the file's comment block. The two
/// job assertions guard the same kind of deletion: a job that stops existing leaves no run to go red.
/// </remarks>
public sealed class CiWorkflowTests
{
    private const string WorkflowPath = ".github/workflows/ci.yml";

    [Fact]
    public void Main_is_checked_on_push()
    {
        var triggers = Triggers(File.ReadAllText(WorkflowFile(RepositoryRoot())));

        Assert.Contains("push:", triggers, StringComparison.Ordinal);

        // The branches list is what makes the push trigger cover main; a push trigger with no filter
        // would also cover main, but this workflow pins it so a throwaway branch costs nothing.
        Assert.Matches(
            new Regex(@"push:\s*\r?\n(?:[ \t]+.*\r?\n)*?[ \t]+branches:\s*\[[^\]]*\bmain\b", RegexOptions.CultureInvariant),
            triggers);
    }

    [Fact]
    public void The_image_check_is_a_job_of_its_own()
    {
        var workflow = File.ReadAllText(WorkflowFile(RepositoryRoot()));

        Assert.Contains("runtime-images:", workflow, StringComparison.Ordinal);
        Assert.Contains("run: ./scripts/verify-images.sh", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_auth_gate_is_a_job_of_its_own()
    {
        var workflow = File.ReadAllText(WorkflowFile(RepositoryRoot()));

        Assert.Contains("auth-flows:", workflow, StringComparison.Ordinal);
        Assert.Contains("run: ./scripts/verify-auth.sh", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The text between the top-level <c>on:</c> key and the next top-level key, so a <c>branches:</c>
    /// under some future step cannot satisfy the assertion.
    /// </summary>
    private static string Triggers(string workflow)
    {
        var start = workflow.IndexOf("\non:", StringComparison.Ordinal);

        Assert.True(start >= 0, "the workflow has no on: block");

        var end = workflow.IndexOf("\npermissions:", start, StringComparison.Ordinal);

        Assert.True(end > start, "the on: block has no end");

        return workflow[start..end];
    }

    private static string WorkflowFile(string root) => Path.Combine(root, ".github", "workflows", "ci.yml");

    /// <summary>
    /// Walks up from the test binary to the solution directory. The RAG project copies the corpus next
    /// to the binary, so stopping at the first directory with a knowledge-base would find a build
    /// artifact instead of the checkout.
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
