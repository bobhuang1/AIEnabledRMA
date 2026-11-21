using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AIEnabledRma.Mcp;

/// <summary>
/// Everything the tools need, registered once for both transports. The transport itself is the only
/// thing the two hosts decide differently, so a tool cannot end up covered on one and not the other.
/// </summary>
public static class McpServices
{
    /// <remarks>
    /// The Production configuration rule runs here, at registration, and takes the environment as a
    /// parameter for the same reason the tools are registered here: the stdio entry point and the HTTP
    /// host each build their own Program, and a check added to one of them is a check the other does
    /// not have. A host left on a development database password refuses to be constructed at all,
    /// rather than starting and failing at its first query.
    /// </remarks>
    public static IServiceCollection AddRmaMcpServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // The callers know the environment before the services exist, so the refusal happens while
        // the host is being composed and cannot be skipped by a transport that forgets to start a
        // hosted service.
        if (environment.IsProduction())
        {
            // No model-provider check: the tool server's tools evaluate stored data and policy and
            // never call a chat model, so it has no Ai section to configure.
            ProductionConfiguration.EnsureSafe(configuration, checkAiProvider: false);
        }

        services.AddRmaDbContext(configuration);
        services.AddRmaData(configuration);

        // Every tool call is audited and counted against the caller's per-minute budget at one
        // choke point (the tools/call filter), so a tool added later is covered without being
        // wrapped. The customer tools additionally cap customer lookups per caller per day.
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<McpBudgetOptions>()
            .Bind(configuration.GetSection(McpBudgetOptions.SectionName));

        // The counter is in PostgreSQL, not in this process: two replicas of the HTTP transport
        // would otherwise each hand out the full allowance, and the limit would multiply by the
        // replica count while every replica looked compliant. Both transports use the same meter,
        // so stdio and HTTP cannot disagree about what a caller has spent.
        services.AddSingleton<ICallMeter, PostgresCallMeter>();
        services.AddSingleton<CallBudget>();
        services.AddScoped<IToolAuditSink, EfToolAuditSink>();
        services.AddToolCallGuard();

        // Registered explicitly as well as through WithToolsFromAssembly: these classes take scoped
        // dependencies (the DbContext through their repositories), so how the SDK activates them is
        // not something to leave to a convention.
        services.AddScoped<DeviceLookupTools>();
        services.AddScoped<CustomerLookupTools>();
        services.AddScoped<PolicyTools>();

        return services;
    }
}
