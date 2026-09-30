using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIEnabledRma.Data.Migrations
{
    /// <summary>
    /// Moves RMA reference allocation from a read-then-increment in application code to a
    /// database sequence.
    ///
    /// The original implementation selected the highest existing number for the year and
    /// returned that plus one. Two requests that both read before either inserted were handed
    /// the same reference, and one of them failed on the unique index. The comment above the
    /// method already described a sequence that did not exist.
    ///
    /// nextval is atomic and never repeats, so concurrent callers are safe by construction
    /// rather than by luck. The unique index stays as the backstop.
    /// </summary>
    ///
    /// <remarks>
    /// The align step is shared with <see cref="RmaNumberSequence.AlignAsync"/> rather than
    /// duplicated here. That is a deliberate trade against the usual rule of freezing migration
    /// code: the sequence is invisible to the EF model, so the migration is the only place it
    /// is created, and a second copy of the alignment query would eventually disagree with the
    /// one the seeding path runs.
    /// </remarks>
    public partial class CreateRmaNumberSequence : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CACHE 1 is deliberate. A larger cache hands out contiguous blocks per session, so
            // an aborted transaction would burn numbers and leave visible gaps in a series that
            // customers read aloud over the phone. Gaps are cosmetically odd; a number handed to
            // two customers is not recoverable.
            migrationBuilder.Sql(
                """
                CREATE SEQUENCE IF NOT EXISTS rma_number_seq
                    AS integer START WITH 1 MINVALUE 1 NO MAXVALUE CACHE 1;
                """);

            // Start above whatever is already stored, so migrating a populated database does
            // not re-issue a reference that is in use.
            migrationBuilder.Sql(RmaNumberSequence.AlignSql);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS rma_number_seq;");
    }
}
