namespace AIEnabledRma.Tests;

/// <summary>
/// The restore drill. These assertions are about the script's shape, not about PostgreSQL: what a
/// dump can do is proven by running <c>scripts/restore-drill.sh</c>, and what is asserted here is
/// that the script cannot quietly stop proving it — the failure mode that matters is a drill that
/// keeps passing while no longer restoring anything.
/// </summary>
public sealed class RestoreDrillScriptTests
{
    [Fact]
    public void The_drill_dumps_restores_migrates_and_compares_the_same_database()
    {
        var script = Script("restore-drill.sh");

        // Dump: the pack's own format, from the seeded database.
        Assert.Contains("pg_dump -U \"$DB_USER\" -Fc", script, StringComparison.Ordinal);

        // The file has to leave the server and come back, or the drill only proves the server can
        // read its own /tmp.
        Assert.Contains("docker cp", script, StringComparison.Ordinal);

        // Restore into a different database, failing on any skipped object.
        Assert.Contains("CREATE DATABASE", script, StringComparison.Ordinal);
        Assert.Contains("pg_restore", script, StringComparison.Ordinal);
        Assert.Contains("--exit-on-error", script, StringComparison.Ordinal);

        // Migrate it, and assert the history survived the round trip.
        Assert.Contains("dbadmin \"$SCRATCH_DB\" migrate", script, StringComparison.Ordinal);
        Assert.Contains("pending", script, StringComparison.Ordinal);

        // Read it back through the application's context and compare every count with the source.
        Assert.Contains("dbadmin \"$SCRATCH_DB\" stats", script, StringComparison.Ordinal);

        // The assembly the publish produces. A rename is a drill that dies on its first command
        // with "dotnet-...dll does not exist", which is a test failure rather than a discovery.
        Assert.Contains("AIEnabledRma.Tools.DbAdmin.dll", script, StringComparison.Ordinal);
        Assert.Contains("diff -u", script, StringComparison.Ordinal);
        Assert.Contains("seed-counts.txt", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drill_refuses_to_pass_on_an_empty_database()
    {
        var script = Script("restore-drill.sh");

        // A dump of nothing restores perfectly. Without this, a seeder that stopped writing would
        // turn the drill into a test of pg_dump's exit code.
        Assert.Contains("^  rma_requests:[[:space:]]+[1-9]", script, StringComparison.Ordinal);
        Assert.Contains("the dump would restore no returns", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drill_cleans_up_after_itself()
    {
        var script = Script("restore-drill.sh");

        Assert.Contains("trap cleanup EXIT", script, StringComparison.Ordinal);
        Assert.Contains("docker rm -f \"$SERVER\"", script, StringComparison.Ordinal);
        Assert.Contains("docker network rm \"$NETWORK\"", script, StringComparison.Ordinal);

        // The publish leaves root-owned files in the working directory, and an EXIT trap under
        // `set -e` turns a failed rm into a failed drill: it did, on the first CI run, after every
        // check had passed. The tree goes from inside a container, and cleanup cannot set the
        // status.
        Assert.Contains("find /work -mindepth 1 -delete", script, StringComparison.Ordinal);
        Assert.Contains("rm -rf \"$WORK\" >/dev/null 2>&1 || true", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verify_all_script_knows_the_restore_step()
    {
        var gate = Script("verify-all.sh");

        Assert.Contains("restore)", gate, StringComparison.Ordinal);
        Assert.Contains("./scripts/restore-drill.sh", gate, StringComparison.Ordinal);
        Assert.Contains("expected suite, auth, images or restore", gate, StringComparison.Ordinal);

        // In the default set, not only reachable by name: the drill is the way back from a lost
        // database, and a gate somebody has to remember is the state this replaced.
        Assert.Contains("set -- suite auth images restore", gate, StringComparison.Ordinal);
    }

    private static string Script(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", name);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).ReplaceLineEndings("\n");
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find scripts/{name} above the test output.");
    }
}
