using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Audit;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace AIEnabledRma.Mcp.Security;

/// <summary>
/// Runs every tool call: checks the privileged-tool permission and the caller's budgets, invokes
/// the tool, and writes one audit row whatever the outcome.
/// </summary>
/// <remarks>
/// Kept independent of the SDK's request context so it can be tested directly; the filter
/// registered in <c>Program</c> only extracts the caller, session and arguments and calls in.
/// Budgets are held in memory per process. That is enough for one stdio process per client and
/// for a single HTTP instance; a scaled-out HTTP deployment needs a shared store.
/// </remarks>
public sealed class ToolCallGuard(
    IToolAuditSink audit,
    IOptions<McpSecurityOptions> options,
    TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recentCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _customerLookups = new(StringComparer.Ordinal);

    public async ValueTask<CallToolResult> RunAsync(
        McpCaller caller,
        string? sessionId,
        string tool,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        Func<CancellationToken, ValueTask<CallToolResult>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(next);

        var settings = options.Value;
        var started = Stopwatch.GetTimestamp();
        var at = time.GetUtcNow();

        CallToolResult result;
        McpToolCallOutcome outcome;

        if (tool == McpSecurityOptions.RevealTool && !(caller.IsAuthenticated && caller.Has(settings.SupportRole)))
        {
            outcome = McpToolCallOutcome.RefusedPermission;
            result = Refusal(
                $"{McpSecurityOptions.RevealTool} needs an authenticated caller with the "
                + $"'{settings.SupportRole}' role. Work with the masked details, or ask the customer.");
        }
        else if (!TryConsumeBudget(caller, sessionId, tool, at, settings, out var budgetMessage))
        {
            outcome = McpToolCallOutcome.RefusedBudget;
            result = Refusal(budgetMessage);
        }
        else
        {
            try
            {
                result = await next(cancellationToken);
                outcome = result.IsError == true ? McpToolCallOutcome.ToolError : McpToolCallOutcome.Ok;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await WriteAsync(McpToolCallOutcome.Failed, null);
                throw;
            }
        }

        await WriteAsync(outcome, outcome == McpToolCallOutcome.Ok ? ResultCountOf(result) : null);
        return result;

        Task WriteAsync(McpToolCallOutcome o, int? count) => audit.RecordAsync(
            new McpToolCall
            {
                Id = Guid.NewGuid(),
                AtUtc = at,
                Tool = Truncate(tool, 64),
                Caller = Truncate(caller.Id, 256),
                SessionId = sessionId is null ? null : Truncate(sessionId, 128),
                ArgumentHashes = HashArguments(arguments),
                ResultCount = count,
                Outcome = o.ToString(),
                LatencyMs = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds),
            },
            CancellationToken.None);
    }

    private bool TryConsumeBudget(
        McpCaller caller,
        string? sessionId,
        string tool,
        DateTimeOffset now,
        McpSecurityOptions settings,
        out string message)
    {
        var window = _recentCalls.GetOrAdd(caller.Id, _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            while (window.Count > 0 && now - window.Peek() >= TimeSpan.FromMinutes(1))
            {
                window.Dequeue();
            }

            if (window.Count >= settings.CallsPerMinute)
            {
                message = $"Rate limit: at most {settings.CallsPerMinute} tool calls per minute. Wait and retry.";
                return false;
            }

            if (McpSecurityOptions.CustomerDataTools.Contains(tool))
            {
                var key = $"{caller.Id}|{sessionId}";
                var used = _customerLookups.AddOrUpdate(key, 1, (_, n) => n + 1);
                if (used > settings.CustomerLookupsPerSession)
                {
                    _customerLookups.AddOrUpdate(key, 0, (_, n) => n - 1);
                    message =
                        $"This session has used its {settings.CustomerLookupsPerSession} customer lookups. "
                        + "Start a new session for a new customer conversation.";
                    return false;
                }
            }

            window.Enqueue(now);
        }

        message = string.Empty;
        return true;
    }

    /// <summary>
    /// Each argument becomes the first 16 hex characters of SHA-256 over its JSON. Equal values
    /// hash equally, so probing is visible in the audit trail, but the value is not recoverable
    /// from a short hash of a high-entropy input and is never written.
    /// </summary>
    public static string HashArguments(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var node = new JsonObject();
        if (arguments is not null)
        {
            foreach (var (name, value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText()));
                node[Truncate(name, 64)] = Convert.ToHexStringLower(bytes)[..16];
            }
        }

        return node.ToJsonString();
    }

    /// <summary>Reads a <c>count</c> or <c>matchCount</c> from the structured result, if present.</summary>
    public static int? ResultCountOf(CallToolResult result)
    {
        JsonElement content;
        if (result.StructuredContent is JsonElement { ValueKind: JsonValueKind.Object } structured)
        {
            content = structured;
        }
        else if (result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text is { Length: > 0 and < 1_000_000 } text
                 && text.TrimStart().StartsWith('{'))
        {
            // Tools without an output schema return their JSON as a text block.
            try
            {
                using var doc = JsonDocument.Parse(text);
                content = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        foreach (var name in new[] { "matchCount", "count" })
        {
            if (content.TryGetProperty(name, out var value) && value.TryGetInt32(out var n))
            {
                return n;
            }
        }

        return null;
    }

    private static CallToolResult Refusal(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
