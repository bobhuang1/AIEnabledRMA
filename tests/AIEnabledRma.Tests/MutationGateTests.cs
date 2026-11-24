using System.Text.Json;

namespace AIEnabledRma.Tests;

/// <summary>
/// The nightly mutation gate. Its properties are files and shell text, and each assertion here is a
/// decision that would otherwise live only in a comment: which types are mutated, that the run is
/// scheduled rather than remembered, and that the number it produces has a budget it cannot exceed.
/// </summary>
/// <remarks>
/// The reason this exists at all: mutation checking was a hand step, and a hand step is what gets
/// skipped when a change is rushed. A gate that silently stopped running — a removed schedule, a
/// config pointing at files that no longer exist — would leave that regression invisible, which is
/// worse than never having had it.
/// </remarks>
public sealed class MutationGateTests
{
    [Fact]
    public void The_gate_runs_nightly_and_runs_the_script()
    {
        var workflow = RepositoryText(".github", "workflows", "mutation.yml");

        Assert.Contains("schedule:", workflow, StringComparison.Ordinal);
        Assert.Contains("cron:", workflow, StringComparison.Ordinal);
        Assert.Contains("run: ./scripts/mutation.sh", workflow, StringComparison.Ordinal);

        // A nightly job that nobody can also start by hand is a job whose first failure is met at
        // three in the morning, a day later.
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);

        var runbook = RepositoryText(".github", "workflows", "ci.yml");

        // Per-push CI stays what it is: mutation testing is thousands of mutants, and putting it on
        // every push would trade the suite's minutes for a number nobody reads daily.
        Assert.DoesNotContain("./scripts/mutation.sh", runbook, StringComparison.Ordinal);
    }

    // The globs are prefixed with **/ because Stryker resolves them from the test project it is run
    // in, not from the project under test: a bare "Rules/RmaPolicyEvaluator.cs" matched nothing and
    // silently mutated the whole Domain project instead, which is a nightly run of hours rather than
    // minutes and a budget nobody can attribute to a file.
    [Theory]
    [InlineData("mutation/stryker-domain.json", "AIEnabledRma.Domain.csproj", "**/Rules/RmaPolicyEvaluator.cs")]
    [InlineData("mutation/stryker-domain.json", "AIEnabledRma.Domain.csproj", "**/Operations/Operations.cs")]
    [InlineData("mutation/stryker-domain.json", "AIEnabledRma.Domain.csproj", "**/Operations/PaymentReconciler.cs")]
    [InlineData("mutation/stryker-domain.json", "AIEnabledRma.Domain.csproj", "**/Operations/Retention.cs")]
    [InlineData("mutation/stryker-privacy.json", "AIEnabledRma.Privacy.csproj", "**/PiiRedactor.cs")]
    public void The_configs_mutate_the_types_the_gate_promises(string config, string project, string file)
    {
        var root = JsonDocument.Parse(RepositoryText(config)).RootElement.GetProperty("stryker-config");

        Assert.Equal(project, root.GetProperty("project").GetString());

        var mutated = root.GetProperty("mutate").EnumerateArray()
            .Select(entry => entry.GetString())
            .ToList();

        Assert.Contains(file, mutated);

        // A report is what the budget is counted from.
        Assert.Contains("json", root.GetProperty("reporters").EnumerateArray().Select(r => r.GetString()));

        // Coverage-based test selection is off, and a test filter does that job instead: Stryker's
        // coverage capture fails against this test project in the SDK container (the run says so and
        // disables it), so the filter is what keeps a nightly run to minutes. A filter that stops
        // matching a renamed test class is a survivor count that rises, which the budget catches.
        Assert.Equal("off", root.GetProperty("coverage-analysis").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("test-case-filter").GetString()));

        // Stryker's own threshold must not fail the run before the budget is compared: the budget is
        // the number in the repository, and two thresholds disagreeing is how a gate reports two
        // different answers for one run.
        Assert.Equal(0, root.GetProperty("thresholds").GetProperty("break").GetInt32());
    }

    [Fact]
    public void The_budget_is_a_number_the_script_compares_against()
    {
        var script = RepositoryText("scripts", "mutation.sh");

        Assert.Contains("mutation/baseline.json", script, StringComparison.Ordinal);
        Assert.Contains("if [ \"$SURVIVED\" -gt \"$budget_survived\" ]", script, StringComparison.Ordinal);
        Assert.Contains("if [ \"$NO_COVERAGE\" -gt \"$budget_uncovored\" ]", script, StringComparison.Ordinal);

        // The count comes from the report, not from a summary line: a log is one formatting change
        // away from reporting a number nobody verified.
        Assert.Contains("\"Survived\"", script, StringComparison.Ordinal);
        Assert.Contains("\"NoCoverage\"", script, StringComparison.Ordinal);

        // No report means the run proved nothing, and a gate that treats a missing report as zero
        // survivors is the exact failure this file exists to prevent.
        Assert.Contains("no JSON report for", script, StringComparison.Ordinal);
        Assert.Contains("the reports covered no files", script, StringComparison.Ordinal);

        var baseline = JsonDocument.Parse(RepositoryText("mutation/baseline.json")).RootElement;

        Assert.True(baseline.GetProperty("survivors").GetInt32() >= 0);
        Assert.True(baseline.GetProperty("noCoverage").GetInt32() >= 0);
        Assert.False(string.IsNullOrWhiteSpace(baseline.GetProperty("measured").GetString()));
    }

    [Fact]
    public void The_tool_version_is_pinned_where_every_other_tool_is()
    {
        var manifest = JsonDocument.Parse(RepositoryText(".config", "dotnet-tools.json")).RootElement;

        var stryker = manifest.GetProperty("tools").GetProperty("dotnet-stryker");

        // A floating mutation tool changes the number this gate compares, which would make a red
        // nightly indistinguishable from a tool upgrade.
        Assert.Matches(@"^\d+\.\d+\.\d+$", stryker.GetProperty("version").GetString()!);
        Assert.False(stryker.GetProperty("rollForward").GetBoolean());
    }

    /// <summary>
    /// The live operation suites have to run for their mutants to be killed; without a database the
    /// gate would count every operation mutation as a survivor and the budget would lose its meaning.
    /// </summary>
    [Fact]
    public void The_run_starts_the_database_its_live_suites_need()
    {
        var script = RepositoryText("scripts", "mutation.sh");

        Assert.Contains("pgvector/pgvector:pg16", script, StringComparison.Ordinal);
        Assert.Contains("RMA_TEST_CONNECTION=", script, StringComparison.Ordinal);
        Assert.Contains("pg_isready", script, StringComparison.Ordinal);

        // And tidies up without letting that decide the result — the failure mode the restore drill
        // already met once, with a green run reported as red.
        Assert.Contains("trap cleanup EXIT", script, StringComparison.Ordinal);
        Assert.Contains("find /work -mindepth 1 -delete", script, StringComparison.Ordinal);
    }

    private static string RepositoryText(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !(Directory.Exists(Path.Combine(directory.FullName, "knowledge-base"))
                    && File.Exists(Path.Combine(directory.FullName, "AIEnabledRma.slnx"))))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
