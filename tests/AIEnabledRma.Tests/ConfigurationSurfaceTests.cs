using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Mcp;

namespace AIEnabledRma.Tests;

/// <summary>
/// The shipped configuration is a published interface: an operator edits <c>appsettings.json</c> or
/// an environment variable and expects the behaviour to change. A key nothing binds reads as a
/// working setting and changes nothing — the <c>McpSecurity</c> block was one, with a
/// <c>SupportRole</c> sitting beside the grant that really admitted the unmask tool — and a property
/// the binder fills while no line reads it is the same defect one step further in.
/// </summary>
/// <remarks>
/// Both hosts' files are checked, because the tool server and the wizard read the same policy types
/// and a key one of them drops is a key the other still describes. The second fact reads the source
/// rather than the configuration, because binding is not use.
///
/// What the source scan cannot see: a property whose name another type also uses, since it counts
/// mentions of the name rather than of the member. That is why the first fact exists as well — a key
/// whose property has gone is caught there, whatever its name, and the two disagree only for a name
/// that is already live elsewhere (removing <c>WarrantyTierPolicy.CurrencyCode</c> was found with a
/// per-type grep, not by this test).
/// </remarks>
public sealed class ConfigurationSurfaceTests
{
    /// <summary>The files whose option-shaped sections this repository owns.</summary>
    private static readonly string[] Files =
    [
        Path.Combine("src", "AIEnabledRma.Mcp", "appsettings.json"),
        Path.Combine("src", "AIEnabledRma.Web", "appsettings.json"),
    ];

    /// <summary>
    /// Sections that bind to a type, and which type. Logging and ConnectionStrings are deliberately
    /// absent: they are the framework's shape, and asserting them would be testing ASP.NET.
    /// </summary>
    private static readonly Dictionary<string, Type> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RmaPolicy"] = typeof(RmaPolicyOptions),
        ["DeviceLookup"] = typeof(DeviceLookupOptions),
        ["CustomerLookup"] = typeof(CustomerLookupOptions),
    };

    /// <summary>Every type a checked section binds, including the ones nested inside <c>Mcp</c>.</summary>
    private static readonly Type[] OptionTypes =
    [
        typeof(RmaPolicyOptions),
        typeof(WarrantyTierPolicy),
        typeof(ProductRepairPrice),
        typeof(ProblemCauseRule),
        typeof(DeviceLookupOptions),
        typeof(CustomerLookupOptions),
        typeof(McpHttpOptions),
        typeof(McpBudgetOptions),
    ];

    [Fact]
    public void Every_published_key_names_a_property_something_binds()
    {
        var root = RepositoryRoot();
        var unresolved = new List<string>();

        foreach (var file in Files)
        {
            var path = Path.Combine(root, file);
            Assert.True(File.Exists(path), $"a shipped configuration file is missing at {path}");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var settings = document.RootElement;

            foreach (var section in Sections)
            {
                Assert.True(
                    settings.TryGetProperty(section.Key, out var value),
                    $"{file} has no {section.Key} section, so the type {section.Value.Name} describes is unconfigured");

                Walk(value, section.Value, section.Key, unresolved);
            }

            // The tool server's own section. The web host does not serve tools, so it may have none.
            if (!settings.TryGetProperty("Mcp", out var mcp))
            {
                continue;
            }

            foreach (var entry in mcp.EnumerateObject())
            {
                switch (entry.Name)
                {
                    case "Transport":
                        // Read before either host is built, by McpTransportMode rather than a binder.
                        WalkTransport(entry.Value, file, unresolved);
                        break;

                    case "Http":
                        Walk(entry.Value, typeof(McpHttpOptions), "Mcp:Http", unresolved);
                        break;

                    case "Budget":
                        Walk(entry.Value, typeof(McpBudgetOptions), "Mcp:Budget", unresolved);
                        break;

                    default:
                        unresolved.Add($"{file}: Mcp:{entry.Name}");
                        break;
                }
            }
        }

        Assert.True(
            unresolved.Count == 0,
            "the shipped configuration has keys nothing binds, so an operator who edits them changes "
            + "nothing: "
            + string.Join(", ", unresolved));
    }

    [Fact]
    public void Every_option_property_is_read_somewhere_outside_its_declaration()
    {
        var source = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .Aggregate((left, right) => left + "\n" + right);

        // The declarations themselves are not evidence of use: strip every one-line property body
        // before counting, so a member that appears only in its own `public … { get; set; }` fails.
        var declarations = new Regex(@"public\s+[^;{}\r\n]*\{\s*(?: private\s+)?get;[^}]*\}");

        var withoutDeclarations = declarations.Replace(source, string.Empty);

        var unread = OptionTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => (Type: type, Property: property)))
            .Where(member => !Regex.IsMatch(
                withoutDeclarations,
                @"\b" + Regex.Escape(member.Property.Name) + @"\b"))
            .Select(member => $"{member.Type.Name}.{member.Property.Name}")
            .ToList();

        Assert.True(
            unread.Count == 0,
            "these settings are bound and never read, so they are knobs that change nothing: "
            + string.Join(", ", unread));
    }

    /// <summary>
    /// Walks one JSON object against the type it binds, so a key with no property is named by its
    /// dotted path rather than by the file it hides in.
    /// </summary>
    private static void Walk(JsonElement element, Type type, string path, List<string> unresolved)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var itemType = type.IsArray
                ? type.GetElementType()!
                : type.IsGenericType
                    ? type.GetGenericArguments()[0]
                    : null;

            if (itemType is null)
            {
                unresolved.Add($"{path} is an array and {type.Name} is not");
                return;
            }

            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                Walk(item, itemType, $"{path}[{index}]", unresolved);
                index++;
            }

            return;
        }

        // A scalar or an enum name: the property itself was checked by the caller.
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in element.EnumerateObject())
        {
            var member = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(property => string.Equals(property.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

            if (member is null)
            {
                unresolved.Add($"{path}:{entry.Name} ({type.Name} has no such setting)");
                continue;
            }

            Walk(entry.Value, member.PropertyType, $"{path}:{entry.Name}", unresolved);
        }
    }

    /// <summary>
    /// <c>Mcp:Transport</c> has no options type: <see cref="McpTransportMode"/> reads it directly and
    /// refuses anything that is not one of its two modes, so the value is what has to be checked.
    /// </summary>
    private static void WalkTransport(JsonElement element, string file, List<string> unresolved)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            unresolved.Add($"{file}: Mcp:Transport must be a string");
            return;
        }

        var value = element.GetString();

        if (!string.Equals(value?.Trim(), McpTransportMode.Stdio, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value?.Trim(), McpTransportMode.Http, StringComparison.OrdinalIgnoreCase))
        {
            unresolved.Add($"{file}: Mcp:Transport is '{value}', which McpTransportMode refuses");
        }
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
