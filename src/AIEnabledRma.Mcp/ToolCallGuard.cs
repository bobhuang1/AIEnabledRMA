using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIEnabledRma.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIEnabledRma.Mcp;

/// <summary>
/// The one place an MCP tool call passes through, whichever tool it is.
/// </summary>
/// <remarks>
/// The audit row and the per-minute counter used to live inside the customer tools, so
/// check_warranty_coverage, get_fee_quote and every device lookup ran unaudited and unthrottled:
/// an agent could walk the device catalog as fast as it liked and leave no evidence it had. That
/// is the whole point of an audit trail, so the control belongs where the tool name is not a
/// thing anyone has to remember. A filter on the tools/call request covers the tools that exist
/// and the one added tomorrow.
///
/// Only the customer tools keep a second, narrower control of their own: the session cap on
/// customer lookups, which is about what a call is for rather than how many calls there are.
/// </remarks>
public sealed class ToolCallGuard(
    IServiceProvider services,
    CallBudget budget,
    TimeProvider clock,
    ILogger<ToolCallGuard> logger)
{
    /// <summary>A stdio session has no identity provider; P2 adds the authenticated caller.</summary>
    public const string CallerName = "stdio:local";

    /// <summary>
    /// The properties a tool response uses to say how many things it found. Read rather than
    /// guessed: a result count that disagrees with the answer is worse than no count at all.
    /// </summary>
    private static readonly string[] CountProperties = ["count", "matchCount", "totalCount"];

    public async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        CancellationToken cancellationToken)
    {
        var tool = request.Params?.Name ?? "(unnamed)";
        var arguments = request.Params?.Arguments;
        var started = Stopwatch.GetTimestamp();

        if (!budget.TryConsumeCall(out var refusal))
        {
            // Refused before the work: the tool is not invoked at all, so the row says the call
            // happened and did nothing.
            await RecordAsync(tool, arguments, 0, started, wasRefused: true, cancellationToken);

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = refusal! }],
            };
        }

        CallToolResult result;
        try
        {
            result = await next(request, cancellationToken);
        }
        catch
        {
            // A tool that threw is still a call that happened, and "it errored" is a fact the
            // audit trail should hold.
            await RecordAsync(tool, arguments, 0, started, wasRefused: true, cancellationToken);
            throw;
        }

        await RecordAsync(
            tool,
            arguments,
            CountOf(result),
            started,
            wasRefused: result.IsError == true,
            cancellationToken);

        return result;
    }

    /// <summary>
    /// The audit row for a call that has already been decided. Writes through a scope of its
    /// own, because the tool's scope is disposed with the call and the sink writes through EF.
    /// </summary>
    private async Task RecordAsync(
        string tool,
        IDictionary<string, JsonElement>? arguments,
        int resultCount,
        long startedTimestamp,
        bool wasRefused,
        CancellationToken cancellationToken)
    {
        var record = new ToolCallRecord
        {
            Tool = tool,
            Caller = CallerName,
            ArgumentHashes = arguments is null
                ? []
                : arguments
                    .OrderBy(a => a.Key, StringComparer.Ordinal)
                    .Select(a => $"{a.Key}:{Hash(a.Value.GetRawText())}")
                    .ToList(),
            ResultCount = resultCount,
            LatencyMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            Refused = wasRefused,
            AtUtc = clock.GetUtcNow(),
        };

        try
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<IToolAuditSink>()
                .RecordAsync(record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A missing row is worth a lot, but not a customer's lookup: the call has already
            // happened and its answer is correct. The failure is loud, and names the tool only.
            logger.LogError(ex, "Could not record the audit row for {Tool}.", tool);
        }
    }

    private static int CountOf(CallToolResult result)
    {
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } structured
            && TryCount(structured, out var named))
        {
            return named;
        }

        // A tool that declares no output schema has its return value sent as a JSON text block
        // rather than as structured content. Read the same object from there, so the recorded
        // count does not depend on which of the two shapes the SDK chose.
        foreach (var block in result.Content)
        {
            if (block is not TextContentBlock { Text: { Length: > 0 } text })
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && TryCount(document.RootElement, out var parsed))
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // A block that is not JSON — an error message, say — carries no count.
            }
        }

        return 0;
    }

    private static bool TryCount(JsonElement element, out int count)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (CountProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out count))
            {
                return true;
            }
        }

        count = 0;
        return false;
    }

    /// <summary>
    /// Argument name and a truncated hash, never the value: an audit trail that keeps the
    /// arguments becomes a second copy of the customer data it exists to protect.
    /// </summary>
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}

/// <summary>Puts the guard on every tools/call request the server handles.</summary>
public static class ToolCallGuardRegistration
{
    public static IServiceCollection AddToolCallGuard(this IServiceCollection services)
    {
        services.AddSingleton<ToolCallGuard>();

        // Configured through the options pipeline rather than a filter instance, so the guard
        // is resolved from the container and shares the session's single CallBudget.
        services.AddOptions<McpServerOptions>().Configure<ToolCallGuard>((options, guard) =>
            options.Filters.Request.CallToolFilters.Add(
                next => (request, cancellationToken) =>
                    guard.InvokeAsync(request, next, cancellationToken)));

        return services;
    }
}
