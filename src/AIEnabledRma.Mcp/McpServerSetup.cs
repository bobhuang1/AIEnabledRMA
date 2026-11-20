using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Mcp.Security;
using AIEnabledRma.Mcp.Tools;
using ModelContextProtocol.Server;

namespace AIEnabledRma.Mcp;

/// <summary>Registration shared by the stdio and HTTP transports.</summary>
public static class McpServerSetup
{
    /// <summary>
    /// Data access, tools, the tool-call guard and the audit sink. Every tool call passes through
    /// <see cref="ToolCallGuard"/>, whatever the transport.
    /// </summary>
    public static IMcpServerBuilder AddRmaMcpServer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRmaDbContext(configuration);
        services.AddRmaData(configuration);

        services.AddOptions<McpSecurityOptions>()
            .Bind(configuration.GetSection(McpSecurityOptions.SectionName))
            .Validate(o => o.CallsPerMinute > 0 && o.CustomerLookupsPerSession > 0, "McpSecurity budgets must be positive.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.LocalCallerName), "McpSecurity:LocalCallerName is required.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IToolAuditSink, EfToolAuditSink>();
        services.AddSingleton<ToolCallGuard>();

        services.AddScoped<DeviceLookupTools>();
        services.AddScoped<CustomerLookupTools>();
        services.AddScoped<PolicyTools>();

        return services.AddMcpServer()
            .WithToolsFromAssembly()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var guard = context.Services!.GetRequiredService<ToolCallGuard>();
                var security = context.Services!.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpSecurityOptions>>().Value;
                var caller = McpCaller.From(context.User, security);
                var arguments = context.Params?.Arguments is { } args
                    ? args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal)
                    : null;

                return await guard.RunAsync(
                    caller,
                    context.Server.SessionId ?? StdioSessionId,
                    context.Params?.Name ?? "(unnamed)",
                    arguments,
                    ct => next(context, ct),
                    cancellationToken);
            }));
    }

    /// <summary>A stdio process serves exactly one client session, so the process is the session.</summary>
    private static readonly string StdioSessionId = "stdio-" + Guid.NewGuid().ToString("N")[..12];
}
