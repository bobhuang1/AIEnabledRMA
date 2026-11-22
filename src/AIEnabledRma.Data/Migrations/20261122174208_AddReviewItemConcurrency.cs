using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIEnabledRma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewItemConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // xmin is PostgreSQL's own system column, the id of the transaction that last wrote
            // the row, and review_items already has one because every table does. The scaffolder
            // emits an AddColumn for it, which would try to create a user column of that name and
            // fail on the system-column conflict. The migration stays empty so the snapshot and
            // the database agree; the token is enforced from the model mapping, not from DDL.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the column belongs to PostgreSQL, not to this application.
        }
    }
}
