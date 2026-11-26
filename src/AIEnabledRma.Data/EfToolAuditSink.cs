using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Data;

/// <summary>
/// Writes the audit row for an agent call.
/// </summary>
/// <remarks>
/// A failed audit write must not fail the tool call: the answer has already been computed and
/// withholding it would not undo the disclosure. The failure is left to the caller's logging,
/// where an operator can see that the trail has a hole.
/// </remarks>
public sealed class EfToolAuditSink(RmaDbContext db) : IToolAuditSink
{
    public async Task RecordAsync(ToolCallRecord record, CancellationToken cancellationToken)
    {
        db.McpToolCalls.Add(new McpToolCall
        {
            Id = Guid.NewGuid(),
            Tool = record.Tool,
            Caller = record.Caller,
            ArgumentHashes = string.Join(',', record.ArgumentHashes),
            Reason = record.Reason,
            ResultCount = record.ResultCount,
            LatencyMs = record.LatencyMs,
            Refused = record.Refused,
            AtUtc = record.AtUtc,
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
