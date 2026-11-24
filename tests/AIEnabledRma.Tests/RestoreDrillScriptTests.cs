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

        // Dump: through the deployment's own backup, from the seeded database. The drill used to run
        // pg_dump itself, which proved the dump format and said nothing about whether a copy ever
        // reached a destination outside the host -- it restored a file it had made a second earlier.
        Assert.Contains("\"$REPO_ROOT/scripts/backup.sh\"", script, StringComparison.Ordinal);
        Assert.Contains("backup backup", script, StringComparison.Ordinal);
        Assert.Contains("backup fetch", script, StringComparison.Ordinal);

        // What is restored comes back from the destination, and the local dumps are deleted first so
        // it cannot be one of them.
        Assert.Contains("rm -f \"$STAGING\"/*.dump", script, StringComparison.Ordinal);
        Assert.Contains("docker cp", script, StringComparison.Ordinal);

        // The backup is pointed at a compose file rather than at a container name, so it takes the
        // same `docker compose exec` branch the nightly timer takes -- which a container of the
        // drill's own, named by hand, never did.
        Assert.Contains("COMPOSE_FILE=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("BACKUP_DB_CONTAINER", script, StringComparison.Ordinal);

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

    [Fact]
    public void The_drill_counts_the_destination_without_ls()
    {
        var script = Script("restore-drill.sh");

        // "Nothing matched" is a normal answer here: the destination holds ciphertext only, and a
        // first run finds it empty. `ls` exits 2 on an unmatched glob, and under `set -e` with
        // pipefail a command substitution that returns 2 ends the drill -- which is what its first
        // CI run did, right after it had put two encrypted dumps off the host. A nullglob turns no
        // match into an empty list instead.
        Assert.DoesNotContain("ls \"$OFFHOST\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ls \"$STAGING\"", script, StringComparison.Ordinal);
        Assert.Contains("shopt -s nullglob", script, StringComparison.Ordinal);
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
