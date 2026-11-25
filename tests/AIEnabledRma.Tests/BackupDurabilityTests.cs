using System.Text.RegularExpressions;

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
    /// <summary>
    /// The nightly unit reaches the database with <c>docker compose exec postgres</c>, and `postgres`
    /// is a name that exists only because the pack defines it. Renaming the service in the pack, or
    /// exec'ing a name the pack does not define, turns the 01:20 backup into "no such service" — and
    /// nothing else in the repository would have noticed, because the drill used to name a container
    /// of its own instead.
    /// </summary>
    [Fact]
    public void The_backup_execs_the_service_the_pack_defines()
    {
        var script = Script();
        var pack = RepositoryFile("docker-compose.prod.yml").ReplaceLineEndings("\n");

        Assert.Equal(DatabaseService(), ServiceExecTarget(script));

        // The credentials pg_dump reads are the ones that service puts in the container's
        // environment; the script passes no -h/-U of its own.
        Assert.Contains("POSTGRES_USER: ${POSTGRES_USER:?set POSTGRES_USER}", pack, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_DB: ${POSTGRES_DB:-rmaprod}", pack, StringComparison.Ordinal);

        // One route to the database, so there is no second branch for a caller to take and no
        // untested one for the nightly unit to be surprised by.
        Assert.DoesNotContain("docker exec -i", script, StringComparison.Ordinal);
        Assert.DoesNotContain("BACKUP_DB_CONTAINER", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// What leaves the host is ciphertext. The destination -- a bucket, a backup host, a mounted
    /// disk -- is read by everyone its access policy admits, which is a different set of people from
    /// the ones allowed to read the database, and a plain dump there is every return, address and
    /// payment reference in one file. The key sits beside rclone.conf rather than in prod.env, which
    /// is copied into every container.
    /// </summary>
    [Fact]
    public void The_backup_encrypts_what_leaves_and_fetch_decrypts_it()
    {
        var script = Script();

        // age v1 to an X25519 identity: the payload is authenticated, and the header names the
        // format. The CBC it replaced could be altered in place at the destination and said nothing
        // about what wrote it.
        Assert.Contains("age --encrypt --recipient \"$age_recipient\"", script, StringComparison.Ordinal);
        Assert.Contains("age --decrypt --identity \"$(winpath \"$BACKUP_AGE_IDENTITY\")\"", script, StringComparison.Ordinal);
        Assert.Contains("age-keygen -y \"$(winpath \"$BACKUP_AGE_IDENTITY\")\"", script, StringComparison.Ordinal);

        // And nothing writes the old cipher any more: the only openssl left in the script opens the
        // objects that pre-date the change.
        Assert.DoesNotContain("openssl enc -aes-256-cbc -pbkdf2", script, StringComparison.Ordinal);

        // Beside rclone.conf by default: deriving the recipient from that directory is what binds
        // the two credentials, and a test that only saw a path would pass on any path.
        Assert.Contains(
            "BACKUP_AGE_IDENTITY=${BACKUP_AGE_IDENTITY:-$RCLONE_CONFIG_DIR/backup.age.key}",
            script,
            StringComparison.Ordinal);
        Assert.Contains("is missing or empty", script, StringComparison.Ordinal);
        Assert.Contains("does not hold an age identity", script, StringComparison.Ordinal);

        // No unencrypted fallback: prod.env does not carry the key, and nothing encrypts only when a
        // variable happens to be set.
        Assert.DoesNotContain("BACKUP_ENCRYPT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("BACKUP_KEY_FILE=", RepositoryFile("deploy/prod.env.example"), StringComparison.Ordinal);

        // Only objects the key opens are listed, fetched or kept.
        Assert.Contains("--include \"*.dump.enc\"", script, StringComparison.Ordinal);

        // And the unencrypted objects an earlier version wrote age out with the same window, on both
        // sides. Retention that reached only the ciphertext would leave every return readable at the
        // destination for ever, which is the exposure the encryption exists to end.
        Assert.Contains("--include \"*.dump.enc\" --include \"*.dump\"", script, StringComparison.Ordinal);
        Assert.Contains("-name '*.dump.enc' -o -name '*.dump'", script, StringComparison.Ordinal);
        Assert.Contains("does not decrypt back to a readable dump", script, StringComparison.Ordinal);
        Assert.Contains("is not a pg_dump archive", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The drill has to take that same route, or it proves a backup path the timer never runs. Its
    /// throwaway server is therefore a compose service named what the script execs, which is what the
    /// pack calls the database.
    /// </summary>
    [Fact]
    public void The_drill_runs_the_backup_the_way_the_nightly_unit_does()
    {
        var drill = RepositoryFile("scripts/restore-drill.sh").ReplaceLineEndings("\n");
        var service = DatabaseService();

        // No container override: without one, the script can only reach the database through the
        // compose file it is given, which is the branch the unit takes.
        Assert.DoesNotContain("BACKUP_DB_CONTAINER", drill, StringComparison.Ordinal);
        Assert.Contains("COMPOSE_FILE=", drill, StringComparison.Ordinal);
        Assert.Contains("ENV_FILE=", drill, StringComparison.Ordinal);

        // Started by compose, as that same service name, so `docker compose exec` finds it.
        Assert.Contains($"\n  {service}:", drill, StringComparison.Ordinal);
        Assert.Contains($"up -d {service}", drill, StringComparison.Ordinal);

        // And the drill checks the route it took, so a script that quietly stops using compose fails
        // here instead of passing on the format of a dump.
        Assert.Contains("docker compose exec postgres", drill, StringComparison.Ordinal);
    }

    /// <summary>
    /// The encryption is worth what the drill can show about it: the destination is opened with the
    /// wrong passphrase and has to refuse, and no readable dump may sit beside the ciphertext. A
    /// script that copied the dump and renamed it would pass every other check here.
    /// </summary>
    [Fact]
    public void The_drill_shows_the_destination_cannot_be_read_without_the_key()
    {
        var drill = RepositoryFile("scripts/restore-drill.sh").ReplaceLineEndings("\n");

        Assert.Contains("BACKUP_AGE_IDENTITY=\"$IDENTITY_FILE\"", drill, StringComparison.Ordinal);
        Assert.Contains("BACKUP_AGE_IDENTITY=\"$WRONG_IDENTITY_FILE\"", drill, StringComparison.Ordinal);
        Assert.Contains("fetch with the wrong identity returned a dump", drill, StringComparison.Ordinal);

        // No plaintext object at the destination, and the drill's own copies are ciphertext.
        Assert.Contains("no *.dump object was written there", drill, StringComparison.Ordinal);
        Assert.Contains("*.dump.enc", drill, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drill_restores_the_newest_off_host_copy_rather_than_a_fresh_dump()
    {
        var drill = RepositoryFile("scripts/restore-drill.sh");

        Assert.Contains("\"$REPO_ROOT/scripts/backup.sh\"", drill, StringComparison.Ordinal);
        Assert.Contains("backup backup", drill, StringComparison.Ordinal);
        Assert.Contains("backup fetch", drill, StringComparison.Ordinal);

        // The local copies go before the fetch, so the restore cannot quietly use one of them.
        Assert.Contains("rm -f \"$STAGING\"/*.dump", drill, StringComparison.Ordinal);

        // `fetch` returns the decrypted file, so the comparison against the object it came from
        // names the ciphertext: the drill has to prove it restored the second upload, not merely
        // that some dump arrived.
        Assert.Contains("[ \"$fetched.enc\" = \"$second\" ]", drill, StringComparison.Ordinal);

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

        // The key, and how to make one: documented beside the destination rather than left to a
        // reader to invent.
        Assert.Contains("BACKUP_AGE_IDENTITY", readme, StringComparison.Ordinal);
        Assert.Contains("$RCLONE_CONFIG_DIR/backup.age.key", readme, StringComparison.Ordinal);
        Assert.Contains("age-keygen -o /etc/aienabledrma/backup.age.key", readme, StringComparison.Ordinal);

        // And the passphrase of the objects written before the migration, with the window it can be
        // retired in: a reader who deletes it on the day of the upgrade loses a fortnight of copies.
        Assert.Contains("BACKUP_KEY_FILE", readme, StringComparison.Ordinal);
        Assert.Contains("$RCLONE_CONFIG_DIR/backup.passphrase", readme, StringComparison.Ordinal);
        Assert.Contains("sudo cp deploy/rma-backup.service deploy/rma-backup.timer /etc/systemd/system/", readme, StringComparison.Ordinal);
        Assert.Contains("systemctl enable --now rma-backup.timer", readme, StringComparison.Ordinal);
        Assert.Contains("journalctl -u rma-backup.service", readme, StringComparison.Ordinal);

        // The drill section says what it restores, so a reader does not assume it makes its own dump.
        // Prose wraps, so the sentence is asserted with the wrapping taken out of it.
        var prose = readme.ReplaceLineEndings(" ");

        Assert.Contains("restores what the deployment's own backup wrote off the host", prose, StringComparison.Ordinal);

        // And how the dump reaches the database, which is the command the drill and the timer share.
        Assert.Contains(
            "docker compose -f docker-compose.prod.yml --env-file prod.env exec -T postgres",
            prose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_purge_removes_the_unencrypted_objects_and_lists_them_first()
    {
        var script = Script().ReplaceLineEndings("\n");

        // A dump uploaded before the encryption existed is readable at the destination until
        // retention ages it out: a fortnight of every return in the database. The purge is the way
        // to take it off now, and it names what it is about to delete — a silent delete leaves an
        // operator unable to tell one stale object from a destination nobody ever purged.
        Assert.Contains("purge-plaintext", script, StringComparison.Ordinal);
        Assert.Contains(
            "rclone lsf \"$REMOTE\" --files-only --include \"*.dump\" | sort",
            script,
            StringComparison.Ordinal);
        Assert.Contains("holds ciphertext only", script, StringComparison.Ordinal);

        // And it deletes exactly that pattern, then asks again. The encrypted objects are the
        // backups: a purge that reached them would take the only copy off the destination.
        Assert.Contains("rclone delete \"$REMOTE\" --include \"*.dump\"\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rclone delete \"$REMOTE\" --include \"*.dump\" --include", script, StringComparison.Ordinal);

        // And asked again afterwards: a run that printed a list and changed nothing has to fail
        // rather than report a purge that did not happen.
        Assert.Contains("still lists *.dump objects after the purge", script, StringComparison.Ordinal);

        // It opens nothing, so it does not need the key: the host that has lost its passphrase is
        // the one with the most plaintext to remove.
        Assert.Contains("if [ \"$MODE\" != purge-plaintext ] && [ \"$MODE\" != list ]; then", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The purge is the one mode that destroys something at the destination, and the only way to find
    /// out what it would take was to let it run: the list it prints was printed on the way to a
    /// delete. A dry run prints the same list and stops, which is the difference between being able to
    /// ask and having to trust.
    /// </summary>
    [Fact]
    public void The_purge_can_be_read_before_it_deletes()
    {
        var script = Script().ReplaceLineEndings("\n");

        // Parsed, and refused everywhere else: a flag quietly accepted for `backup` would let an
        // operator believe a rehearsal had happened when the command ran exactly as usual.
        Assert.Contains("if [ \"${2:-}\" = --dry-run ]; then", script, StringComparison.Ordinal);
        Assert.Contains("--dry-run applies to purge-plaintext", script, StringComparison.Ordinal);
        Assert.Contains("unknown argument", script, StringComparison.Ordinal);

        // It says what it did not do, so a run of a script cannot mistake a rehearsal for a purge.
        Assert.Contains("dry run: nothing was deleted.", script, StringComparison.Ordinal);

        // And the delete is below the early exit: a dry run cannot reach it, and neither can the
        // re-check that would otherwise report a purge of objects that are all still there.
        var dryRun = script.IndexOf("if [ \"$DRY_RUN\" = yes ]; then", StringComparison.Ordinal);
        var delete = script.IndexOf("rclone delete \"$REMOTE\" --include \"*.dump\"\n", StringComparison.Ordinal);

        Assert.True(dryRun > 0, "the purge has no --dry-run branch");
        Assert.True(delete > dryRun, "the purge deletes before it checks for --dry-run");
    }

    [Fact]
    public void The_staging_copy_is_private_and_one_run_cannot_overwrite_another()
    {
        var script = Script().ReplaceLineEndings("\n");

        // umask only applies when mkdir creates the directory. A staging directory that already
        // existed at 0755 leaves the plaintext dump readable by every account on the host for the
        // seconds between pg_dump and the cipher, so the mode is set rather than assumed.
        Assert.Contains("chmod 700 \"$BACKUP_STAGING\"", script, StringComparison.Ordinal);

        // A key file written by an editor on Windows carries a CR. A passphrase that silently ends
        // in one is a different key from the same characters typed on the host, and nobody would
        // think to look for it.
        Assert.Contains("head -n 1 \"$BACKUP_KEY_FILE\" | tr -d '\\r'", script, StringComparison.Ordinal);

        // The object name is fixed to the second and `rclone copyto` overwrites: a second run inside
        // the same second would replace the copy the first published, which is a night's backup lost
        // without a word.
        Assert.Contains("is already in $BACKUP_STAGING", script, StringComparison.Ordinal);

        // Inspecting a destination opens nothing, so it needs no key: a host that has lost the
        // passphrase still has to be able to say what the destination is holding.
        Assert.Contains("if [ \"$MODE\" != purge-plaintext ] && [ \"$MODE\" != list ]; then", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The migration has two halves, and the second one is only worth anything while old copies
    /// exist: every object at the destination older than the change is in a format the new code did
    /// not write, inside a retention window that guarantees there are some. The branch that opens
    /// them is chosen by what the object is rather than by its name, because both formats carry the
    /// same `*.dump.enc` name and an operator fetching one does not know which it is.
    /// </summary>
    [Fact]
    public void The_dumps_written_before_the_migration_stay_restorable_until_the_window_closes()
    {
        var script = Script().ReplaceLineEndings("\n");

        // `openssl enc -salt` writes these eight bytes ahead of the ciphertext; age writes its own
        // header there instead. A check on the name would be a check on nothing.
        Assert.Contains(
            "if [ \"$(head -c 8 \"$source\")\" != 'Salted__' ]; then",
            script,
            StringComparison.Ordinal);

        // The old cipher survives in exactly one place, and only to read.
        Assert.Contains("openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000", script, StringComparison.Ordinal);
        Assert.Contains("-in \"$(winpath \"$source\")\"", script, StringComparison.Ordinal);

        // Without its passphrase the legacy branch refuses and names the file, rather than returning
        // nothing or falling through to the identity that cannot open it.
        Assert.Contains("was written before the migration to age", script, StringComparison.Ordinal);
        Assert.Contains("which is missing or empty. Nothing was staged.", script, StringComparison.Ordinal);

        // And the passphrase is not required up front: a key being retired must not be able to stop
        // the nightly backup, which is what a guard around every mode would have done.
        Assert.Contains(
            "BACKUP_KEY_FILE=${BACKUP_KEY_FILE:-$RCLONE_CONFIG_DIR/backup.passphrase}",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("backup_passphrase=", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A drill that only ever exercises the format written today proves nothing about the fortnight
    /// of copies the migration left behind. This is the gate on both paths: what leaves the host is
    /// age, an object in the old format still opens, and the passphrase is needed for that object and
    /// for nothing else.
    /// </summary>
    [Fact]
    public void The_drill_proves_both_the_new_format_and_the_one_before_it()
    {
        var drill = RepositoryFile("scripts/restore-drill.sh").ReplaceLineEndings("\n");

        // Only age-keygen can make an identity, so its absence is a failure rather than a skip.
        Assert.Contains("age-keygen -o \"$IDENTITY_FILE\"", drill, StringComparison.Ordinal);

        // What left the host is recognised by its own header, not by the name it was given.
        Assert.Contains("age-encryption.org/v1", drill, StringComparison.Ordinal);

        // An object in the old format is written deliberately, named so `fetch` chooses it, and
        // compared with the dump it was made from -- restoring something is not the claim.
        Assert.Contains("openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt", drill, StringComparison.Ordinal);
        Assert.Contains("LEGACY_ENC=rma-20991231T235959Z.dump.enc", drill, StringComparison.Ordinal);
        Assert.Contains(
            "cmp -s \"$STAGING/$legacy_fetched\" \"$STAGING/$fetched\"",
            drill,
            StringComparison.Ordinal);

        // The branch is shown to need its passphrase, and the nightly run shown not to.
        Assert.Contains("run_backup \"$NO_KEY\" fetch", drill, StringComparison.Ordinal);
        Assert.Contains("run_backup \"$NO_KEY\" backup", drill, StringComparison.Ordinal);
    }

    private static string Script() => RepositoryFile("scripts/backup.sh");

    /// <summary>
    /// The service name the backup execs, read out of the script rather than repeated here: the
    /// assertion is that the pack and the drill agree with the script, not that all three say a name
    /// this file happens to spell.
    /// </summary>
    /// <summary>
    /// The service the pack runs its PostgreSQL image as, found by that image rather than by a name
    /// spelled here: the files under test have to agree with each other, not with this file.
    /// </summary>
    private static string DatabaseService()
    {
        var pack = RepositoryFile("docker-compose.prod.yml").ReplaceLineEndings("\n");
        var match = Regex.Match(pack, @"(?m)^  ([A-Za-z0-9_-]+):\n    image: pgvector/pgvector");

        Assert.True(match.Success, "docker-compose.prod.yml no longer runs a pgvector/pgvector service");

        return match.Groups[1].Value;
    }

    private static string ServiceExecTarget(string script)
    {
        var match = Regex.Match(script, @"--env-file ""\$ENV_FILE"" exec -T (\S+)");

        Assert.True(match.Success, "scripts/backup.sh no longer execs a compose service");

        return match.Groups[1].Value;
    }

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
