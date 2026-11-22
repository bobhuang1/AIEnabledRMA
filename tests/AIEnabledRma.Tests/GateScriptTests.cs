namespace AIEnabledRma.Tests;

/// <summary>
/// The gates are shell scripts, so their properties are strings. Each assertion here is a decision
/// that would otherwise survive only in a comment.
/// </summary>
/// <remarks>
/// The workflow is readable by anyone with the repository; a failing run's log is not, and the checks
/// these scripts perform are the only readable account of what the gate does. The assertions are
/// deliberately about intent rather than formatting: the local gate runs the same checks CI
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
    public void The_local_gate_runs_the_same_checks_as_ci()
    {
        var script = Script("verify-all.sh");

        Assert.Contains("./scripts/verify-auth.sh", script, StringComparison.Ordinal);
        Assert.Contains("./scripts/verify-images.sh", script, StringComparison.Ordinal);
        Assert.Contains("dotnet test AIEnabledRma.slnx --no-build", script, StringComparison.Ordinal);

        // The build-and-test job also refuses a model that disagrees with its migrations and a
        // dependency with a known advisory. Without those here, a local pass means less than a CI
        // pass, and the difference stays invisible until the deployment or the next audit. The
        // workflow is the source of the list, so this fails when CI gains a step the local gate
        // does not have as well as when the local gate drops one.
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));

        foreach (var check in new[]
                 {
                     "dotnet tool restore",
                     "dotnet ef migrations has-pending-model-changes",
                     "dotnet list AIEnabledRma.slnx package --vulnerable --include-transitive",
                     "has the following vulnerable packages",
                 })
        {
            Assert.Contains(check, workflow, StringComparison.Ordinal);
            Assert.Contains(check, script, StringComparison.Ordinal);
        }

        // The restore drill is a gate now, not a script somebody remembers: last in the default set
        // here, and its own job in CI. Migrations are forward-only and `reset --force` drops the
        // database, so this is the only check that speaks for the way back.
        Assert.Contains("./scripts/restore-drill.sh", script, StringComparison.Ordinal);
        Assert.Contains("set -- suite auth images restore", script, StringComparison.Ordinal);
        Assert.Contains("restore-drill:", workflow, StringComparison.Ordinal);
        Assert.Contains("run: ./scripts/restore-drill.sh", workflow, StringComparison.Ordinal);

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
    public void The_image_gate_boots_both_corpus_hosts_on_their_corpus()
    {
        var script = Script("verify-images.sh");

        // Counting files proves the COPY list, not the load. The gate starts each image that reads
        // the corpus through its own entrypoint in Production — the environment where a drifted
        // corpus is a startup failure — and reads the count the host logs. The RAG host is the
        // second reader: leaving it out let its corpus be present and unusable with nothing
        // noticing, which is the state the image file checks cannot see.
        Assert.Contains("boot_smoke \"Web\" \"$WEB_IMAGE\"", script, StringComparison.Ordinal);
        Assert.Contains("boot_smoke \"RAG\" \"$RAG_IMAGE\"", script, StringComparison.Ordinal);
        Assert.Contains("AIEnabledRma.Web.dll", script, StringComparison.Ordinal);
        Assert.Contains("AIEnabledRma.Rag.dll", script, StringComparison.Ordinal);

        // The entry point reaches the container as an environment variable, because the drift case
        // runs inside a single-quoted `sh -c`: a `$assembly` there expands to nothing, and the host
        // runs `dotnet` with no argument — which exits non-zero, so the assertion below passes for
        // the wrong reason while never loading a corpus at all.
        Assert.Contains("-e \"SMOKE_ASSEMBLY=$assembly\"", script, StringComparison.Ordinal);
        Assert.Contains("exec dotnet \"$SMOKE_ASSEMBLY\"", script, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Production", script, StringComparison.Ordinal);

        // The web image boots in Production, where an operator identity provider is required. A
        // smoke environment without it would exercise a host that cannot start, and the count the
        // gate reads would never appear.
        Assert.Contains("-e OperatorAuth__Authority=", script, StringComparison.Ordinal);
        Assert.Contains("-e OperatorAuth__ClientId=", script, StringComparison.Ordinal);
        Assert.Contains("\"$expected_articles article(s)\"", script, StringComparison.Ordinal);
        Assert.Contains("0 problem(s)", script, StringComparison.Ordinal);

        // The second case is the one that makes the first meaningful: the same image, one article
        // removed, must exit non-zero and say which manifest it does not match.
        Assert.Contains("head -1 /app/knowledge-base/REVIEWED.sha256", script, StringComparison.Ordinal);
        Assert.Contains("REVIEWED.sha256", script, StringComparison.Ordinal);
        Assert.Contains("does not match", script, StringComparison.Ordinal);
        Assert.Contains("if [ \"$rc\" -ne 0 ]", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The boot smoke must not need a database somebody else already started, and must not run against
    /// one whose development password the host refuses in Production.
    /// </summary>
    [Fact]
    public void The_image_gate_starts_its_own_database_for_the_boot_smoke()
    {
        var script = Script("verify-images.sh");

        Assert.Contains("PG_IMAGE=${PG_IMAGE:-pgvector/pgvector:pg16}", script, StringComparison.Ordinal);
        Assert.Contains("--name \"$SMOKE_PG\"", script, StringComparison.Ordinal);
        Assert.Contains("pg_isready", script, StringComparison.Ordinal);
        Assert.Contains("smoke_cleanup", script, StringComparison.Ordinal);
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
        Assert.Contains("check_image \"RAG\"", script, StringComparison.Ordinal);
        Assert.Contains("$MCP_IMAGE\" empty", script, StringComparison.Ordinal);
        Assert.Contains("$WEB_IMAGE\" corpus", script, StringComparison.Ordinal);
        Assert.Contains("$RAG_IMAGE\" corpus", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The image comparison that answered "what did the ORM move actually buy" was made by hand, so
    /// the answer lived in somebody's shell history. This pins the script that reproduces it and the
    /// progress entry that points at it: a number nobody can rerun is a number about the last time
    /// somebody remembered the commands.
    /// </summary>
    [Fact]
    public void The_rag_image_comparison_is_one_command_and_the_progress_entry_names_it()
    {
        var script = Script("rag-image-diff.sh");

        // Two revisions, each built from its own archived tree. A script that compared a revision
        // against the working tree would measure uncommitted work as well, which is the failure this
        // replaces.
        Assert.Contains("[ \"$#\" -eq 2 ]", script, StringComparison.Ordinal);
        Assert.Contains("git archive --format=tar", script, StringComparison.Ordinal);
        Assert.Contains("src/AIEnabledRma.Rag/Dockerfile", script, StringComparison.Ordinal);

        // The bytes come from the image's own layers, not from the size docker prints rounded: the
        // move this measures was just under 2% of the pull, which that column cannot show at all.
        Assert.Contains("docker save", script, StringComparison.Ordinal);

        // A saved layer is the compressed blob itself, so the uncompressed figure has to come from
        // decompressing it. Summing the blobs and calling that "uncompressed" puts the compression
        // number on both lines and makes two measurements of one quantity look like a saving.
        Assert.Contains("gzip.decompress", script, StringComparison.Ordinal);
        // The whole label, not the words "app layer": the header comment carries those too, and a
        // mutation that deleted the printed line passed every assertion when it was matched that way.
        Assert.Contains("app layer (COPY --from=build /app", script, StringComparison.Ordinal);
        Assert.Contains("whole image", script, StringComparison.Ordinal);
        Assert.Contains("AIEnabledRma.Rag.dll", script, StringComparison.Ordinal);

        // A differing layer outside the app layer means the app-layer delta is not the whole story,
        // and the script says so rather than printing a tidy number.
        Assert.Contains("a layer outside it changed", script, StringComparison.Ordinal);

        var progress = File.ReadAllText(Path.Combine(RepositoryRoot(), "progress.md"));

        Assert.Contains("scripts/rag-image-diff.sh 05f80b5 8e74e61", progress, StringComparison.Ordinal);
    }

    /// <summary>
    /// The production pack has to resolve. It is a compose file whose required values are all
    /// placeholders in the example env; a renamed variable or a broken anchor turns a deployment into
    /// a parse error, and nothing else in this repository reads that file. Resolving and starting are
    /// different claims, so the second is asserted below.
    /// </summary>
    [Fact]
    public void Ci_resolves_the_production_pack_and_the_image_gate_starts_it()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains(
            "docker compose -f docker-compose.prod.yml --env-file deploy/prod.env.example config",
            workflow,
            StringComparison.Ordinal);

        // The boot lives in the image gate, which this job runs: a resolve step that nobody follows
        // with a start is the gap this pair closes.
        Assert.Contains("./scripts/verify-images.sh", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// All three application services are brought up from the pack and the example env, not from a
    /// hand-built container with a throwaway database. The service definitions are what a deployment
    /// runs, so a required value the example forgot or an environment the host now refuses has to
    /// fail here, before anyone points a domain at it. Starting only the web service left the tool
    /// server and the RAG host unverified against their own production settings.
    /// </summary>
    [Fact]
    public void The_image_gate_starts_the_production_services_from_the_example_env()
    {
        var script = Script("verify-images.sh");

        Assert.Contains("docker-compose.prod.yml", script, StringComparison.Ordinal);
        Assert.Contains("--env-file deploy/prod.env.example", script, StringComparison.Ordinal);
        Assert.Contains("up -d --build web mcp rag", script, StringComparison.Ordinal);

        // Each service is waited for by name and by the line it prints, and each is asserted to
        // still be running afterwards: a container that logged and exited has not booted.
        Assert.Contains("prod_wait_for_line web", script, StringComparison.Ordinal);
        Assert.Contains("prod_wait_for_line rag", script, StringComparison.Ordinal);
        Assert.Contains("prod_wait_for_line mcp", script, StringComparison.Ordinal);
        Assert.Contains("for service in web mcp rag", script, StringComparison.Ordinal);
        Assert.Contains("prod_boot_smoke", script, StringComparison.Ordinal);

        // Its own project name and a down that removes the volumes: the check must not adopt a real
        // stack on the machine, and a database left between runs would make the next boot quieter
        // than a deployment's first one.
        Assert.Contains("PROD_PROJECT=rma-image-smoke-prod", script, StringComparison.Ordinal);
        Assert.Contains("down -v --rmi local", script, StringComparison.Ordinal);
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
