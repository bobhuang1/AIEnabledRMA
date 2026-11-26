using System.Reflection;
using System.Text.RegularExpressions;

namespace AIEnabledRma.Tests;

/// <summary>
/// The tool table is how an operator or an agent's author learns what the server answers to, and the
/// runbook and the plan repeat its size. Both are prose, so nothing compiles when they drift — the
/// README named <c>check_coverage</c> and <c>lookup_device</c> long after the server had renamed them
/// to <c>check_warranty_coverage</c> and <c>find_device</c>, and left out two tools that existed, so a
/// reader following the table called names the server does not answer to.
/// </summary>
/// <remarks>
/// The registered names come from the attributes the server actually publishes rather than from a
/// list beside them, so a tool added to a class without a documentation row fails here — which is
/// the direction that matters, because a missing row costs a reader a tool they never learn about
/// while a stale row costs them a failed call.
///
/// What this cannot see: a row whose description no longer matches what the tool does. It checks the
/// names and the counts, not the sentences.
/// </remarks>
public sealed class ToolDocumentationTests
{
    /// <summary>The tool names the MCP server publishes, ordered for a stable message.</summary>
    private static List<string> RegisteredTools()
    {
        var tools = typeof(AIEnabledRma.Mcp.Tools.CustomerLookupTools).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(method => method.GetCustomAttributes(inherit: false))
            .Where(attribute => attribute.GetType().Name == "McpServerToolAttribute")
            .Select(attribute => attribute.GetType().GetProperty("Name")?.GetValue(attribute) as string)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(tools.Count > 0, "the tool server publishes no McpServerTool attribute, so this test would pass vacuously");
        return tools;
    }

    [Fact]
    public void The_readme_tool_table_names_every_published_tool_and_no_other()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));

        // The table under "Tools:" and not every backticked first cell in the file: the retention and
        // seed tables have the same shape and are not tools. The heading is found with a multiline
        // match rather than an index of "\nTools:\n", because the checked-out README is CRLF and that
        // string is not in it.
        var heading = Regex.Match(readme, @"^Tools:[ \t]*\r?$", RegexOptions.Multiline);
        Assert.True(heading.Success, "README.md no longer has a 'Tools:' section");

        var next = Regex.Match(readme[(heading.Index + heading.Length)..], @"^## ", RegexOptions.Multiline);
        var start = heading.Index;
        var end = next.Success ? heading.Index + heading.Length + next.Index : readme.Length;

        var documented = Regex
            .Matches(readme[start..end], @"^\| `([a-z][a-z_]*)` \|", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.True(documented.Count == documented.Distinct(StringComparer.Ordinal).Count(),
            "the README tool table lists a tool twice: " + string.Join(", ", documented));

        var registered = RegisteredTools();

        var undocumented = registered.Except(documented, StringComparer.Ordinal).ToList();
        var unknown = documented.Except(registered, StringComparer.Ordinal).ToList();

        Assert.True(
            undocumented.Count == 0 && unknown.Count == 0,
            "the README tool table and the server disagree — the server publishes "
            + string.Concat(registered.Select(name => name + " "))
            + "; the README documents "
            + string.Concat(documented.Select(name => name + " "))
            + (undocumented.Count > 0 ? "|undocumented: " + string.Join(", ", undocumented) : string.Empty)
            + (unknown.Count > 0 ? "|not a tool: " + string.Join(", ", unknown) : string.Empty));
    }

    /// <summary>
    /// The runbook and the plan both state the size in words rather than deriving it, so the number
    /// is a second place to go stale: the plan said ten after the unmask tool shipped and nine in the
    /// paragraph above it.
    /// </summary>
    [Fact]
    public void The_runbook_and_the_plan_state_the_number_of_tools_the_server_registers()
    {
        var count = RegisteredTools().Count;
        var word = count switch
        {
            9 => "nine",
            10 => "ten",
            11 => "eleven",
            12 => "twelve",
            _ => count.ToString(),
        };

        var root = RepositoryRoot();
        var plan = File.ReadAllText(Path.Combine(root, "plan.md"));
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "mcp.md"));

        Assert.True(
            plan.Contains($"{count} read-only tools", StringComparison.Ordinal),
            $"plan.md does not say the server has {count} read-only tools; a tool added or removed "
            + "moves that sentence as well as this test");

        Assert.True(
            runbook.Contains($"the {word} tools", StringComparison.Ordinal),
            $"docs/mcp.md does not say tools/list answers with the {word} tools, which is the count the "
            + "server publishes");
    }

    /// <summary>
    /// The refusal an operator is most likely to hit is the one caused by copy-pasting the read
    /// scope into the support scope, and it arrives as a bare <c>InvalidOperationException</c> at
    /// startup with no HTTP status attached. The runbook quotes the message verbatim so searching for
    /// the line they saw finds the cause and the fix; this keeps the quote the message the guard
    /// throws rather than one it threw once.
    /// </summary>
    [Fact]
    public void The_runbook_quotes_the_support_scope_refusal_an_operator_would_see()
    {
        var options = new AIEnabledRma.Mcp.McpHttpOptions
        {
            Issuer = "https://identity.test/realms/aienabledrma",
            RequiredScope = "rma:read",
            SupportScope = "rma:read",
            AllowedClientIds = ["rma-agent"],
        };

        var thrown = Assert.Throws<InvalidOperationException>(
            () => options.EnsureUsable(isDevelopment: false));

        var runbook = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "mcp.md"));

        // Whole-string containment: the operator has the exact line in their log, so the runbook has
        // to carry the same one. Matching on "SupportScope" alone would pass while the sentence an
        // operator searches for is nowhere in the file.
        Assert.True(
            runbook.Contains(thrown.Message, StringComparison.Ordinal),
            "docs/mcp.md does not quote the support-scope startup refusal verbatim, so an operator "
            + $"searching for the line they got finds nothing. The guard says: {thrown.Message}");
    }

    /// <summary>
    /// Walks up from the test binary to the directory that holds the solution, because the suite can
    /// be started from a shell, an IDE or CI and none of those share a working directory.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AIEnabledRma.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
