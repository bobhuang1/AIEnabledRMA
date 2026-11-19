namespace AIEnabledRma.Tests;

/// <summary>
/// The gates are shell scripts, so their properties are strings. Each assertion here is a decision
/// that would otherwise survive only in a comment.
/// </summary>
/// <remarks>
/// The workflow is readable by anyone with the repository; a failing run's log is not, and the checks
/// these scripts perform are the only readable account of what the gate does. The assertions are
/// deliberately about intent rather than formatting: the local gate runs the same three checks CI
/// runs, the image gate compares the commit label and knows the tool server carries no corpus, and
/// the auth gate generates an admin password instead of logging in with the one in the repository.
/// </remarks>
public sealed class GateScriptTests
{
    private static string Script(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", name));

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

    [Fact]
    public void The_local_gate_runs_the_same_three_checks_as_ci()
    {
        var script = Script("verify-all.sh");

        Assert.Contains("./scripts/verify-auth.sh", script, StringComparison.Ordinal);
        Assert.Contains("./scripts/verify-images.sh", script, StringComparison.Ordinal);
        Assert.Contains("dotnet test AIEnabledRma.slnx --no-build", script, StringComparison.Ordinal);

        // The database and the skip check travel together: without a connection string the live
        // suites skip, and a green run that ran no database test is the failure the gate exists for.
        Assert.Contains("RMA_TEST_CONNECTION=", script, StringComparison.Ordinal);
        Assert.Contains("Skipped:", script, StringComparison.Ordinal);

        // The runbook points at it, or nobody who reads the documentation finds it.
        var runbook = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "mcp.md"));

        Assert.Contains("./scripts/verify-all.sh", runbook, StringComparison.Ordinal);
    }

    /// <summary>
    /// The "no tests ran" guard has to match the line the runner actually prints.
    /// </summary>
    /// <remarks>
    /// xunit's console summary says <c>Total tests: 412</c>. A check written against <c>Total:</c>
    /// never matches it, so the step that exists to catch a suite that ran nothing failed instead —
    /// and it failed the same way in CI, on a green run, for as long as nobody reached that step.
    /// </remarks>
    [Fact]
    public void The_no_tests_ran_guard_matches_the_summary_the_runner_prints()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains("Total tests:[[:space:]]+[1-9]", workflow, StringComparison.Ordinal);
        Assert.Contains("Total tests:[[:space:]]+[1-9]", Script("verify-all.sh"), StringComparison.Ordinal);

        // And the pattern matches the line the runner prints, not just itself: a guard proved only
        // by appearing in the file can still be wrong, which is exactly what it was. The pattern is
        // POSIX ERE for grep, and .NET has no POSIX character classes, so the one class it uses is
        // translated rather than the assertion skipped.
        var pattern = new System.Text.RegularExpressions.Regex("Total tests:[[:space:]]+[1-9]".Replace("[[:space:]]", "\\s"));

        var summary = string.Join(
            Environment.NewLine,
            ["Test Run Successful.", "Total tests: 412", "     Passed: 412"]);

        Assert.Matches(pattern, summary);
        Assert.DoesNotMatch(pattern, "Total tests: 0");
    }

    [Fact]
    public void The_image_gate_checks_the_commit_label_and_which_host_has_a_corpus()
    {
        var script = Script("verify-images.sh");

        Assert.Contains("org.opencontainers.image.revision", script, StringComparison.Ordinal);
        Assert.Contains("GIT_REVISION=$REVISION", script, StringComparison.Ordinal);

        // One expectation per image, and they differ on purpose: the Web host reads the corpus, the
        // tool server does not. A test that only said "both carry the corpus" is what made the
        // unused copy look verified.
        Assert.Contains("check_image \"MCP\"", script, StringComparison.Ordinal);
        Assert.Contains("$MCP_IMAGE\" empty", script, StringComparison.Ordinal);
        Assert.Contains("$WEB_IMAGE\" corpus", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_auth_gate_generates_its_admin_password()
    {
        var script = Script("verify-auth.sh");

        // The master-realm admin can rewrite every client and user the realm commits, so it must not
        // be the `admin`/`admin` in this repository — the compose default stays for hands-on use, and
        // the gate stops relying on it.
        Assert.Contains("secrets.token_urlsafe", script, StringComparison.Ordinal);
        Assert.Contains("export KEYCLOAK_ADMIN_PASSWORD", script, StringComparison.Ordinal);
        Assert.DoesNotContain("KEYCLOAK_ADMIN_PASSWORD:-admin", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AIEnabledRma.Mcp")]
    [InlineData("AIEnabledRma.Web")]
    public void Each_image_carries_the_commit_it_was_built_from(string project)
    {
        var dockerfile = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", project, "Dockerfile"));

        Assert.Contains("ARG GIT_REVISION", dockerfile, StringComparison.Ordinal);
        Assert.Contains("LABEL org.opencontainers.image.revision=$GIT_REVISION", dockerfile, StringComparison.Ordinal);
    }
}
