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
    /// The restore drill is a job, because it is the only check that speaks for the way back from a
    /// lost database: migrations are forward-only and <c>reset --force</c> drops the schema.
    /// </summary>
    /// <remarks>
    /// It is separate from build-and-test on purpose. It publishes its own tool and moves a dump
    /// between containers, needs neither the SDK nor the service database that job provisions, and
    /// a shared job would make its timeout the other job's.
    /// </remarks>
    [Fact]
    public void The_restore_drill_is_a_job_of_its_own()
    {
        var workflow = File.ReadAllText(WorkflowFile(RepositoryRoot()));

        Assert.Contains("restore-drill:", workflow, StringComparison.Ordinal);
        Assert.Contains("run: ./scripts/restore-drill.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("timeout-minutes: 20", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The solution is built by the SDK it declares, not by whichever one the runner happens to
    /// have installed.
    /// </summary>
    /// <remarks>
    /// Both failures on main before this were one cause: <c>setup-dotnet</c> installed 10.0.x, the
    /// runner image also shipped SDK 11.0.100, and the CLI selected 11 — whose StaticWebAssets
    /// targets fail the Web project with "scoped css bundles do not have matching endpoints".
    /// Installing a version and using it are different things, so the version is pinned where every
    /// dotnet command reads it: global.json. rollForward is latestFeature, which keeps the band at
    /// 10.0 and allows a later patch, rather than accepting the next major.
    /// </remarks>
    [Fact]
    public void The_sdk_is_pinned_to_the_version_the_solution_targets()
    {
        var root = RepositoryRoot();
        var global = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "global.json"))).RootElement.GetProperty("sdk");

        var version = global.GetProperty("version").GetString();

        Assert.NotNull(version);
        Assert.StartsWith("10.0.", version, StringComparison.Ordinal);
        Assert.Equal("latestFeature", global.GetProperty("rollForward").GetString());

        // A prerelease would roll forward past the band the lock files were restored against.
        Assert.False(global.GetProperty("allowPrerelease").GetBoolean());

        // The workflow asks for the same major, so setup-dotnet installs an SDK the pin accepts.
        var workflow = File.ReadAllText(WorkflowFile(root));

        Assert.Contains("dotnet-version: \"10.0.x\"", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed build has to report itself. The first two runs on main both failed at that step and
    /// the API answered only "Process completed with exit code 1", because the compiler's message
    /// lives in a step log that needs a token to read. The step now tees its output, leaves a binlog,
    /// and re-emits the diagnostics as annotations, which are readable without one.
    /// </summary>
    [Fact]
    public void A_failed_build_reports_its_diagnostics_as_annotations()
    {
        var workflow = File.ReadAllText(WorkflowFile(RepositoryRoot()));

        Assert.Contains("-bl:build.binlog", workflow, StringComparison.Ordinal);
        Assert.Contains("| tee build.log", workflow, StringComparison.Ordinal);
        Assert.Contains("::error::$(printf '%s' \"$line\"", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Main's runs are not cancelled by the next push. A cancelled run reports nothing, so cancelling
    /// the only run of a commit on main recreates the state this file exists to prevent: a branch
    /// that ships with no green run to show for it. Pull requests still cancel their predecessor.
    /// </summary>
    [Fact]
    public void Main_runs_are_not_cancelled_by_a_later_push()
    {
        var workflow = File.ReadAllText(WorkflowFile(RepositoryRoot()));

        Assert.DoesNotContain("cancel-in-progress: true", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "cancel-in-progress: ${{ github.event_name == 'pull_request' }}",
            workflow,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The image job shares the buildx cache, and the auth job builds the same tool server image on
    /// another machine. Without a cache each run compiles the SDK stage twice, which is the cost this
    /// workflow's own comment block says to keep down.
    /// </summary>
    [Fact]
    public void The_image_job_shares_the_layer_cache()
    {
        var workflow = File.ReadAllText(WorkflowFile(RepositoryRoot()));

        Assert.Contains("RMA_IMAGE_CACHE: \"1\"", workflow, StringComparison.Ordinal);

        var script = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "scripts", "verify-images.sh"));

        Assert.Contains("--cache-from \"type=gha,scope=$name\"", script, StringComparison.Ordinal);
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
