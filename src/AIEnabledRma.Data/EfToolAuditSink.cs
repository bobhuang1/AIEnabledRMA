using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIEnabledRma.Data;

/// <summary>
/// Writes each MCP tool call to <c>mcp_tool_calls</c> through its own short-lived context, so
/// the audit write never shares change tracking with the tool's own queries.
/// </summary>
public sealed class EfToolAuditSink(IServiceScopeFactory scopes, ILogger<EfToolAuditSink> logger) : IToolAuditSink
{
    public async Task RecordAsync(McpToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RmaDbContext>();
            db.McpToolCalls.Add(call);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An audit failure is logged loudly but does not turn an answered call into an error:
            // the caller already has the result. The tool name and outcome are safe to log;
            // argument hashes are not needed to diagnose a failed insert.
            logger.LogError(ex, "Could not write the audit row for MCP tool {Tool} ({Outcome}).", call.Tool, call.Outcome);
        }
    }
}
