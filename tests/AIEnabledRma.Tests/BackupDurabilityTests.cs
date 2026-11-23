namespace AIEnabledRma.Tests;

/// <summary>
/// The backup claim, at the level of the files that make it: a destination outside the host, a
/// retention window that deletes, a schedule that runs the same command a person does, and a drill
/// that restores what left the host instead of a dump made a second earlier.
/// </summary>
/// <remarks>
/// The drill itself is the live proof — <c>scripts/verify-all.sh restore</c> runs it against a real
/// server, a real rclone copy and a real restore. These assertions are the pairings a live run cannot
/// check: the script's destination is required rather than defaulted, the units and the README name
/// the same command, and the drill takes its dump from <c>scripts/backup.sh</c> rather than from
/// <c>pg_dump</c> of its own, which is the difference between proving the backup path and proving the
/// dump format.
/// </remarks>
public sealed class BackupDurabilityTests
{
    [Fact]
    public void The_backup_will_not_run_without_a_destination_outside_the_host()
    {
        var script = Script();

        // No default: a destination that is quietly this host's disk is the state this ends.
        Assert.Contains("BACKUP_REMOTE=${BACKUP_REMOTE:-}", script, StringComparison.Ordinal);
        Assert.Contains("not a backup; name the destination", script, StringComparison.Ordinal);
        Assert.Contains("exit 2", script, StringComparison.Ordinal);

        // The example env offers the shapes, commented out, because compose does not require them.
        var example = RepositoryFile("deploy/prod.env.example");

        Assert.Contains("# BACKUP_REMOTE=", example, StringComparison.Ordinal);
        Assert.Contains("# BACKUP_KEEP_DAYS=", example, StringComparison.Ordinal);
        Assert.Contains("sftp:backup.example.com", example, StringComparison.Ordinal);
    }

    [Fact]
    public void The_backup_prunes_both_copies_to_the_window()
    {
        var script = Script();

        Assert.Contains("BACKUP_KEEP_DAYS=${BACKUP_KEEP_DAYS:-14}", script, StringComparison.Ordinal);
        Assert.Contains("--min-age \"${BACKUP_KEEP_DAYS}d\"", script, StringComparison.Ordinal);
        Assert.Contains("-mtime \"+$BACKUP_KEEP_DAYS\"", script, StringComparison.Ordinal);

        // Zero days is not a short window, it is a backup kept until the next one.
        Assert.Contains("BACKUP_KEEP_DAYS must be at least 1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_backup_checks_the_dump_before_it_uploads_it()
    {
        var script = Script();

        Assert.Contains("pg_restore --list", script, StringComparison.Ordinal);
        Assert.Contains("refusing to publish it", script, StringComparison.Ordinal);
        Assert.Contains("rclone copyto", script, StringComparison.Ordinal);

        // The image is pinned by digest, like every other image the pack runs.
        Assert.Contains("rclone/rclone:1.68.1@sha256:", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of the request: the drill restores the newest copy that left the host, and its own
    /// dump command is gone. A drill that dumps a fresh file and restores it is green whether or not
    /// anything ever reached the destination.
    /// </summary>
    [Fact]
    public void The_drill_restores_the_newest_off_host_copy_rather_than_a_fresh_dump()
    {
        var drill = RepositoryFile("scripts/restore-drill.sh");

        Assert.Contains("\"$REPO_ROOT/scripts/backup.sh\"", drill, StringComparison.Ordinal);
        Assert.Contains("backup backup", drill, StringComparison.Ordinal);
        Assert.Contains("backup fetch", drill, StringComparison.Ordinal);

        // The local copies go before the fetch, so the restore cannot quietly use one of them.
        Assert.Contains("rm -f \"$STAGING\"/*.dump", drill, StringComparison.Ordinal);
        Assert.Contains("[ \"$fetched\" = \"$second\" ]", drill, StringComparison.Ordinal);

        // No dump of its own, and no "copy out of the container and back in" stand-in for a backup.
        Assert.DoesNotContain("pg_dump -U", drill, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scheduled_units_run_one_backup_nightly()
    {
        var service = RepositoryFile("deploy/rma-backup.service");
        var timer = RepositoryFile("deploy/rma-backup.timer");

        // A service that is not oneshot stays active after the container exits, so the timer fires
        // into a unit that is still running and the backup silently stops happening.
        Assert.Contains("Type=oneshot", service, StringComparison.Ordinal);
        Assert.Contains("scripts/backup.sh backup", service, StringComparison.Ordinal);
        Assert.Contains("EnvironmentFile=/srv/aienabledrma/prod.env", service, StringComparison.Ordinal);

        Assert.Contains("OnCalendar=", timer, StringComparison.Ordinal);
        Assert.Contains("Persistent=true", timer, StringComparison.Ordinal);
        Assert.Contains("WantedBy=timers.target", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_documents_the_destination_the_window_and_the_install()
    {
        var readme = RepositoryFile("README.md");

        Assert.Contains("### Database backups", readme, StringComparison.Ordinal);
        Assert.Contains("BACKUP_REMOTE", readme, StringComparison.Ordinal);
        Assert.Contains("sftp:backup.example.com", readme, StringComparison.Ordinal);
        Assert.Contains("BACKUP_KEEP_DAYS", readme, StringComparison.Ordinal);
        Assert.Contains("sudo cp deploy/rma-backup.service deploy/rma-backup.timer /etc/systemd/system/", readme, StringComparison.Ordinal);
        Assert.Contains("systemctl enable --now rma-backup.timer", readme, StringComparison.Ordinal);
        Assert.Contains("journalctl -u rma-backup.service", readme, StringComparison.Ordinal);

        // The drill section says what it restores, so a reader does not assume it makes its own dump.
        // Prose wraps, so the sentence is asserted with the wrapping taken out of it.
        var prose = readme.ReplaceLineEndings(" ");

        Assert.Contains("restores what the deployment's own backup wrote off the host", prose, StringComparison.Ordinal);
    }

    private static string Script() => RepositoryFile("scripts/backup.sh");

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} above the test output.");
    }
}
