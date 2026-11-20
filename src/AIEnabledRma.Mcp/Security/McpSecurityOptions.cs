namespace AIEnabledRma.Mcp.Security;

/// <summary>Caller identity, budgets and privileged-tool rules for the MCP server.</summary>
public sealed class McpSecurityOptions
{
    public const string SectionName = "McpSecurity";

    /// <summary>
    /// Name recorded for a caller with no authenticated identity. On stdio that is whoever
    /// launched the process, so it is a named local caller rather than "anonymous".
    /// </summary>
    public string LocalCallerName { get; set; } = "local-stdio";

    /// <summary>Tool calls one caller may make in any rolling minute.</summary>
    public int CallsPerMinute { get; set; } = 60;

    /// <summary>Customer-data tool calls one caller may make per session.</summary>
    public int CustomerLookupsPerSession { get; set; } = 30;

    /// <summary>Role (or scope) that unlocks <c>reveal_customer_contact</c>.</summary>
    public string SupportRole { get; set; } = "rma:support";

    /// <summary>Tools that read customer data and count against <see cref="CustomerLookupsPerSession"/>.</summary>
    public static readonly IReadOnlySet<string> CustomerDataTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "find_customer",
        "get_customer_addresses",
        "get_customer_returns",
        "resolve_shipping_address",
        RevealTool,
    };

    /// <summary>The only tool that returns unmasked contact details.</summary>
    public const string RevealTool = "reveal_customer_contact";
}
