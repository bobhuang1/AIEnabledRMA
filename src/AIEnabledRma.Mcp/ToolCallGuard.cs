using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIEnabledRma.Domain.Abstractions;
using Microsoft.AspNetCore.Http;
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
    IServiceScopeFactory scopes,
    CallBudget budget,
    TimeProvider clock,
    ICallerIdentity caller,
    ILogger<ToolCallGuard> logger)
{
    /// <summary>A stdio session has no identity provider and is recorded as one local caller.</summary>
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
        var reason = ReasonOf(arguments);
        var started = Stopwatch.GetTimestamp();

        var budgetDecision = await budget.TryConsumeCallAsync(cancellationToken);

        if (!budgetDecision.Allowed)
        {
            // Refused before the work: the tool is not invoked at all, so the row says the call
            // happened and did nothing.
            await RecordAsync(tool, arguments, reason, 0, started, wasRefused: true);

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = budgetDecision.Refusal! }],
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
            await RecordAsync(tool, arguments, reason, 0, started, wasRefused: true);
            throw;
        }

        await RecordAsync(
            tool,
            arguments,
            reason,
            CountOf(result),
            started,
            wasRefused: result.IsError == true);

        return result;
    }

    /// <summary>
    /// The audit row for a call that has already been decided. Writes through a scope of its
    /// own, because the tool's scope is disposed with the call and the sink writes through EF.
    /// The write is not cancellable: a client that hung up has still been answered.
    /// </summary>
    private async Task RecordAsync(
        string tool,
        IDictionary<string, JsonElement>? arguments,
        string? reason,
        int resultCount,
        long startedTimestamp,
        bool wasRefused)
    {
        var record = new ToolCallRecord
        {
            Tool = tool,
            Caller = caller.Describe(),
            ArgumentHashes = arguments is null
                ? []
                : arguments
                    .OrderBy(a => a.Key, StringComparer.Ordinal)
                    .Select(a => $"{a.Key}:{Hash(a.Value.GetRawText())}")
                    .ToList(),
            Reason = reason,
            ResultCount = resultCount,
            LatencyMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            Refused = wasRefused,
            AtUtc = clock.GetUtcNow(),
        };

        try
        {
            using var scope = scopes.CreateScope();
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

    /// <summary>
    /// How many things the tool said it found, read from the response body. These tools declare
    /// no output schema, so the return value arrives as a JSON text block; a block that is not
    /// JSON — an error message — carries no count.
    /// </summary>
    private static int CountOf(CallToolResult result)
    {
        foreach (var block in result.Content)
        {
            if (block is not TextContentBlock { Text: { Length: > 0 } text })
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (CountProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out var count))
                    {
                        return count;
                    }
                }
            }
            catch (JsonException)
            {
                // Not a response body; keep looking.
            }
        }

        return 0;
    }

    /// <summary>
    /// Argument name and a truncated hash, never the value: an audit trail that keeps the
    /// arguments becomes a second copy of the customer data it exists to protect.
    /// </summary>
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    /// <summary>
    /// The one argument value a row keeps as written, because it is the row's own subject: why a
    /// caller unmasked a record.
    /// </summary>
    /// <remarks>
    /// Every other argument is a search term or an id and is hashed. A reason is not data the call
    /// acted on — it is the justification for the act, and a trail that answered "this caller read
    /// one customer's contact details" without "because they said…" records that it happened and
    /// not whether it should have. The value is trimmed and bounded rather than trusted to be short,
    /// because the column has a length and the caller writes the text.
    /// </remarks>
    private static string? ReasonOf(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null
            || !arguments.TryGetValue("reason", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= MaximumReasonLength ? text : text[..MaximumReasonLength];
    }

    /// <summary>Longest justification the audit row keeps; the column is one width for it.</summary>
    internal const int MaximumReasonLength = 500;
}

/// <summary>Puts the guard on every tools/call request the server handles.</summary>
public static class ToolCallGuardRegistration
{
    /// <param name="supportScope">
    /// The scope that admits the unmask tool, as the HTTP host validated it. Null on stdio, where
    /// there is no token to carry it and therefore no caller who may unmask.
    /// </param>
    public static IServiceCollection AddToolCallGuard(
        this IServiceCollection services,
        string? supportScope = null)
    {
        // The caller is asked for rather than assumed: on HTTP it is the validated token's client
        // and subject, and on stdio there is no HttpContext, so the same class answers for both.
        services.AddHttpContextAccessor();
        services.AddSingleton<ICallerIdentity>(sp => new HttpContextCallerIdentity(
            sp.GetRequiredService<IHttpContextAccessor>(),
            supportScope));
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
