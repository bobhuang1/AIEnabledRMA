using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Triage;

namespace AIEnabledRma.Data;

/// <summary>
/// Writes a refused question to <c>triage_refusals</c>.
/// </summary>
/// <remarks>
/// One row per refusal rather than one per distinct statement: the count is the interesting number
/// — twelve people asking the same undocumented question is a different signal from one — and a
/// counter would need a unique key that had to be normalised before it could be trusted. The report
/// groups, so the rows stay the evidence and the ranking stays a query.
///
/// A failure here is left to the pipeline, which logs it and still refuses the question: the
/// customer's refusal was decided before this call, and no database problem changes it.
/// </remarks>
public sealed class EfTriageRefusalLog(RmaDbContext db) : ITriageRefusalLog
{
    public async Task RecordAsync(TriageRefusal refusal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        db.TriageRefusals.Add(new TriageRefusal
        {
            Id = refusal.Id == Guid.Empty ? Guid.NewGuid() : refusal.Id,
            AtUtc = refusal.AtUtc,
            Reason = refusal.Reason,
            Statement = refusal.Statement,
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
