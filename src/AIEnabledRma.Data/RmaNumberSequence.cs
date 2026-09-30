using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Data;

/// <summary>
/// RMA reference allocation, backed by a PostgreSQL sequence.
/// </summary>
/// <remarks>
/// The sequence is not part of the EF model: it allocates a value rather than describing a
/// column, so there is nothing for <c>RmaDbContext</c> to map. It is created by migration
/// <c>CreateRmaNumberSequence</c> and read through <c>nextval</c> at allocation time.
/// </remarks>
public static class RmaNumberSequence
{
    public const string Name = "rma_number_seq";

    /// <summary>
    /// Points the sequence one past the highest reference already stored, without ever moving
    /// it backwards.
    /// </summary>
    /// <remarks>
    /// Both bounds are load-bearing. The table maximum stops the first generated reference from
    /// colliding with the seeded demo row, which is <c>RMA-2026-00001</c> and would otherwise
    /// be re-issued. The sequence's own high-water mark stops a second call from rewinding to
    /// a number that has already been handed out, which matters because alignment runs after
    /// seeding and again after an operator reset.
    ///
    /// The regex guard keeps the cast safe for any row that does not follow the
    /// RMA-YYYY-NNNNN shape. The digit class is spelled out rather than written as
    /// [0-9]{4} because ExecuteSqlRawAsync treats its SQL as a composite format string, and a
    /// brace there is a format placeholder that throws. The migration path does not format, so
    /// the SQL has to be valid as-is for both callers.
    /// </summary>
    public const string AlignSql = """
        SELECT setval(
            'rma_number_seq',
            GREATEST(
                COALESCE((
                    SELECT MAX(split_part(rma_number, '-', 3)::int)
                    FROM rma_requests
                    WHERE rma_number ~ '^RMA-[0-9][0-9][0-9][0-9]-[0-9]+$'
                ), 0),
                (SELECT CASE WHEN is_called THEN last_value ELSE last_value - 1 END
                 FROM rma_number_seq)
            ) + 1,
            false);
        """;

    /// <summary>
    /// Realigns the sequence with the rows already in the table. Call after any bulk insert of
    /// reference data, and after a schema reset.
    /// </summary>
    public static Task AlignAsync(RmaDbContext db, CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlRawAsync(AlignSql, cancellationToken);
}
