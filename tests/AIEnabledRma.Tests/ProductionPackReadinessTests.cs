using System.Text.RegularExpressions;

namespace AIEnabledRma.Tests;

/// <summary>
/// Readiness for the production pack: the three application services say when they can serve, and
/// the proxy waits for that answer instead of for a container that merely started.
/// </summary>
/// <remarks>
/// Only PostgreSQL had a healthcheck, so `depends_on` meant "the container was created" for web,
/// mcp and rag — a host still loading its corpus, or one that started and immediately failed to
/// bind, was indistinguishable from a serving one to anything that reads compose.
///
/// The probe is asserted at the level of what it does rather than how compose spells it: the same
/// anchor on all three services, an HTTP 200 from <c>/health</c>, and no dependency on a package
/// these images do not carry. curl is not in the Debian ASP.NET image, and installing it into three
/// production images to answer one question is a cost this repository already declined once.
/// </remarks>
public sealed class ProductionPackReadinessTests
{
    private const string Pack = "docker-compose.prod.yml";

    [Theory]
    [InlineData("web")]
    [InlineData("mcp")]
    [InlineData("rag")]
    public void Every_application_service_has_the_readiness_probe(string service)
    {
        var block = ServiceBlock(RepositoryFile(Pack), service);

        Assert.Contains("healthcheck: *http-readiness", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_probe_asks_health_for_a_200_over_http()
    {
        var anchor = ServiceBlock(RepositoryFile(Pack), "http-readiness");

        Assert.Contains("/health", anchor, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 200", anchor, StringComparison.Ordinal);
        Assert.Contains("interval:", anchor, StringComparison.Ordinal);
        Assert.Contains("retries:", anchor, StringComparison.Ordinal);

        // A start period, so a host still reading its corpus is not restarted while it is working.
        Assert.Contains("start_period:", anchor, StringComparison.Ordinal);
    }

    [Fact]
    public void The_probe_does_not_need_a_package_the_images_lack()
    {
        var anchor = ServiceBlock(RepositoryFile(Pack), "http-readiness");

        // curl and wget are not installed in mcr.microsoft.com/dotnet/aspnet, and a probe that needs
        // them would fail every check with "not found" rather than with an unhealthy service.
        Assert.DoesNotContain("curl", anchor, StringComparison.Ordinal);
        Assert.DoesNotContain("wget", anchor, StringComparison.Ordinal);
        Assert.Contains("/dev/tcp/127.0.0.1/8080", anchor, StringComparison.Ordinal);
    }

    /// <summary>
    /// All three hosts map the path the probe calls. A probe against an endpoint no host serves
    /// reports every correctly running service as unhealthy.
    /// </summary>
    [Theory]
    [InlineData("src/AIEnabledRma.Web/Program.cs")]
    [InlineData("src/AIEnabledRma.Rag/Program.cs")]
    [InlineData("src/AIEnabledRma.Mcp/McpHttpHost.cs")]
    public void The_hosts_map_the_path_the_probe_calls(string host)
    {
        Assert.Contains("MapGet(\"/health\"", RepositoryFile(host), StringComparison.Ordinal);
    }

    /// <summary>
    /// A service may only be waited on as healthy if it has a probe. Compose accepts the reference
    /// either way — the project resolves and `up --no-start` creates the container — so pointing
    /// `depends_on` at a service with no healthcheck is found at deployment, where the wait never
    /// finishes. `scripts/verify-compose.sh` asks the resolved project in CI; this asks the file, so
    /// the same edit fails in the suite as well.
    /// </summary>
    [Fact]
    public void Every_service_waited_on_as_healthy_declares_a_healthcheck()
    {
        // Normalised first: `$` matches before a \n but not before the \r of a CRLF line, so the
        // checked-in file would parse as a pack with no services and this test would pass on nothing.
        var compose = RepositoryFile(Pack).ReplaceLineEndings("\n");

        var waits = Dependencies(compose)
            .Where(dependency => dependency.Condition == "condition: service_healthy")
            .ToList();

        // Five today — proxy waits for web and mcp, those two wait for postgres, and the scheduled
        // reconciliation one-shot waits for postgres as well — and the count is pinned so a parse
        // that stops finding them fails here instead of reporting a pack with no waits.
        Assert.Equal(5, waits.Count);

        foreach (var (_, to, _) in waits)
        {
            Assert.Contains(
                "healthcheck:",
                ServiceBlock(compose, to),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The edge had no probe: `docker compose ps` showed it running while nothing asked whether Caddy
    /// could serve. The probe is the admin endpoint on localhost, not a published port — port 80
    /// answers a redirect that a client follows into a certificate handshake, and port 443 cannot
    /// answer before that certificate exists, so probing either calls a working edge dead while ACME
    /// is still working.
    /// </summary>
    [Fact]
    public void The_proxy_has_a_readiness_probe_of_its_own()
    {
        var proxy = ServiceBlock(RepositoryFile(Pack), "proxy");

        Assert.Contains("healthcheck:", proxy, StringComparison.Ordinal);
        Assert.Contains(
            "http://127.0.0.1:2019/config/",
            proxy,
            StringComparison.Ordinal);

        // Not the ports it publishes: neither answers for the reason a probe exists.
        Assert.DoesNotContain("127.0.0.1:80", proxy, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", proxy, StringComparison.Ordinal);

        // A start period, so a certificate fetch in progress is not a restart loop.
        Assert.Contains("start_period:", proxy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every dependency in the pack names a condition, and it is the healthy one. Nothing starts at
    /// "container created", which is the state the probes exist to tell apart from serving; with the
    /// edge now having a probe of its own, anything added behind it can wait for it the same way.
    /// </summary>
    [Fact]
    public void Every_dependency_in_the_pack_waits_for_health()
    {
        var dependencies = Dependencies(RepositoryFile(Pack));

        // Pinned: a parse that stopped finding entries would otherwise pass while checking nothing.
        Assert.Equal(5, dependencies.Count);

        foreach (var dependency in dependencies)
        {
            Assert.Equal("condition: service_healthy", dependency.Condition);
        }
    }

    [Fact]
    public void The_proxy_waits_for_the_services_it_fronts()
    {
        var proxy = ServiceBlock(RepositoryFile(Pack), "proxy");

        Assert.Contains("web:", proxy, StringComparison.Ordinal);
        Assert.Contains("mcp:", proxy, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(proxy, "condition: service_healthy"));
    }

    /// <summary>
    /// The reconciliation sweep is the one service in the pack that is meant to finish. Started by
    /// `up` it would run on every deploy and then sit there as a stopped container; given a restart
    /// policy it would sweep every few seconds and bury the queue with the same review item, so the
    /// two properties are asserted together with the profile that keeps it out of the default start.
    /// </summary>
    [Fact]
    public void The_reconciliation_sweep_is_a_one_shot_outside_the_default_start()
    {
        var sweep = ServiceBlock(RepositoryFile(Pack), "reconcile-payments");

        Assert.Contains("profiles: [\"ops\"]", sweep, StringComparison.Ordinal);
        Assert.Contains("restart: \"no\"", sweep, StringComparison.Ordinal);
        Assert.Contains("command: [\"reconcile-payments\"]", sweep, StringComparison.Ordinal);

        // It reaches the database the way the application services do, from inside the pack.
        Assert.Contains("<<: *app-env", sweep, StringComparison.Ordinal);
        Assert.Contains("condition: service_healthy", sweep, StringComparison.Ordinal);

        // And it asks the provider, so it needs the same Stripe settings the web host has.
        Assert.Contains("Payments__Provider: stripe", sweep, StringComparison.Ordinal);
    }

    /// <summary>
    /// The image the sweep runs from. Without the tool inside it the service starts, fails with
    /// "could not execute because the specified command was not found", and the timer reports a
    /// broken sweep every night for a reason that has nothing to do with payments.
    /// </summary>
    [Fact]
    public void The_sweep_image_runs_the_operator_tool()
    {
        var dockerfile = RepositoryFile("tools/DbAdmin/Dockerfile");

        Assert.Contains("dotnet publish tools/DbAdmin", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"AIEnabledRma.Tools.DbAdmin.dll\"]", dockerfile, StringComparison.Ordinal);

        // Non-root, and pinned by digest like the other images: a tag is whatever was published last.
        Assert.Contains("USER $APP_UID", dockerfile, StringComparison.Ordinal);
        Assert.Contains("@sha256:", dockerfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Dockerfile nobody builds is a Dockerfile that does not work. This one was excluded from the
    /// build context by .dockerignore — that pattern was written when only the web, mcp and rag images
    /// existed — so it failed at its first COPY with "packages.lock.json: not found", and the only
    /// thing that would have noticed is the first night the sweep was supposed to run. Both halves are
    /// pinned here: the image is in the gate that builds the others, and the context still contains
    /// its sources.
    /// </summary>
    [Fact]
    public void The_gate_builds_the_sweep_image_and_the_context_keeps_its_sources()
    {
        var gate = RepositoryFile("scripts/verify-images.sh");

        Assert.Contains("check_image \"Ops\" \"tools/DbAdmin/Dockerfile\" \"$OPS_IMAGE\" corpus", gate, StringComparison.Ordinal);
        Assert.Contains("OPS_IMAGE=${OPS_IMAGE:-", gate, StringComparison.Ordinal);

        var ignore = RepositoryFile(".dockerignore").Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotContain("\ntools/\n", "\n" + ignore + "\n", StringComparison.Ordinal);
    }


    /// <summary>
    /// The README has to say how to install it. `reconcile-payments` was documented as a command an
    /// operator types, which is the state this change exists to replace: a schedule nobody can find
    /// in the deploy notes is a schedule nobody installs.
    /// </summary>
    [Fact]
    public void The_readme_says_how_to_install_the_scheduled_run()
    {
        var readme = RepositoryFile("README.md");

        Assert.Contains("--profile ops run --rm reconcile-payments", readme, StringComparison.Ordinal);
        Assert.Contains("sudo cp deploy/rma-reconcile.service deploy/rma-reconcile.timer /etc/systemd/system/", readme, StringComparison.Ordinal);
        Assert.Contains("systemctl enable --now rma-reconcile.timer", readme, StringComparison.Ordinal);
        Assert.Contains("systemctl list-timers rma-reconcile.timer", readme, StringComparison.Ordinal);
        Assert.Contains("journalctl -u rma-reconcile.service", readme, StringComparison.Ordinal);

        // The exit code that means "nobody could ask the provider" is the one a schedule has to
        // report, not swallow.
        Assert.Contains("exit 2", readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// The units the README tells an operator to copy. A service that is not `oneshot` stays "active"
    /// after the container exits, so the timer fires into a unit that is still running and the sweep
    /// silently stops happening; a timer without `Persistent=true` loses the night the host was down.
    /// </summary>
    [Fact]
    public void The_scheduled_units_run_one_sweep_nightly()
    {
        var service = RepositoryFile("deploy/rma-reconcile.service");
        var timer = RepositoryFile("deploy/rma-reconcile.timer");

        Assert.Contains("Type=oneshot", service, StringComparison.Ordinal);
        Assert.Contains("--env-file prod.env", service, StringComparison.Ordinal);
        Assert.Contains("--profile ops run --rm reconcile-payments", service, StringComparison.Ordinal);

        Assert.Contains("OnCalendar=", timer, StringComparison.Ordinal);
        Assert.Contains("Persistent=true", timer, StringComparison.Ordinal);

        // Without this the timer cannot be enabled, so "systemctl enable --now" installs nothing.
        Assert.Contains("WantedBy=timers.target", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_keeps_its_own_probe()
    {
        var postgres = ServiceBlock(RepositoryFile(Pack), "postgres");

        Assert.Contains("pg_isready", postgres, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every `depends_on` entry in the pack with the condition written under it, read from the file
    /// rather than from the resolved project: a bare list (`depends_on: [web]`) inherits
    /// `service_started`, and compose accepts it, so only the file shows it.
    /// </summary>
    private static IReadOnlyList<(string From, string To, string Condition)> Dependencies(string compose)
    {
        var normalized = compose.ReplaceLineEndings("\n");
        var dependencies = new List<(string From, string To, string Condition)>();

        foreach (Match service in Regex.Matches(normalized, @"^  ([\w-]+):$", RegexOptions.Multiline))
        {
            var name = service.Groups[1].Value;
            var lines = ServiceBlock(normalized, name).ReplaceLineEndings("\n").Split('\n');
            var inDependsOn = false;

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i] == "    depends_on:")
                {
                    inDependsOn = true;
                    continue;
                }

                // The next key at service-key indentation ends the dependency list.
                if (inDependsOn && !lines[i].StartsWith("      ", StringComparison.Ordinal))
                {
                    inDependsOn = false;
                }

                if (!inDependsOn)
                {
                    continue;
                }

                var target = Regex.Match(lines[i], @"^      ([\w-]+):$");

                if (!target.Success)
                {
                    continue;
                }

                var condition = i + 1 < lines.Length ? lines[i + 1].Trim() : string.Empty;
                dependencies.Add((name, target.Groups[1].Value, condition));
            }
        }

        return dependencies;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// One block from the compose file: from its own key at two-space indentation to the next key at
    /// the same indentation. Extension fields (<c>x-...</c>) are at column zero, which is why the
    /// anchor is found by prefix rather than by exact indentation.
    /// </summary>
    private static string ServiceBlock(string compose, string name)
    {
        var lines = compose.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, line =>
            line == $"  {name}:" || line.StartsWith($"x-{name}:", StringComparison.Ordinal));

        Assert.True(start >= 0, $"{Pack} has no '{name}' block.");

        // Three spaces for a service, whose keys sit at four and whose siblings sit at two: a
        // one-space test would run the block on to the end of the file and count every other
        // service's keys as this one's. An extension field's body is indented two, so it uses two.
        var indent = lines[start].StartsWith("x-", StringComparison.Ordinal) ? "  " : "   ";

        var end = start + 1;
        while (end < lines.Length
               && (lines[end].Length == 0
                   || lines[end].StartsWith(indent, StringComparison.Ordinal)))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]);
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
