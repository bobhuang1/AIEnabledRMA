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

        var waits = new List<(string From, string To)>();

        foreach (Match service in Regex.Matches(compose, @"^  ([\w-]+):$", RegexOptions.Multiline))
        {
            var name = service.Groups[1].Value;
            var lines = ServiceBlock(compose, name).ReplaceLineEndings("\n").Split('\n');
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

                if (target.Success
                    && i + 1 < lines.Length
                    && lines[i + 1].Trim() == "condition: service_healthy")
                {
                    waits.Add((name, target.Groups[1].Value));
                }
            }
        }

        // Four today — proxy waits for web and mcp, and each of those waits for postgres — and the
        // count is pinned so a parse that stops finding them fails here instead of reporting a pack
        // with no waits.
        Assert.Equal(4, waits.Count);

        foreach (var (from, to) in waits)
        {
            Assert.Contains(
                "healthcheck:",
                ServiceBlock(compose, to),
                StringComparison.Ordinal);
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

    [Fact]
    public void Postgres_keeps_its_own_probe()
    {
        var postgres = ServiceBlock(RepositoryFile(Pack), "postgres");

        Assert.Contains("pg_isready", postgres, StringComparison.Ordinal);
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
