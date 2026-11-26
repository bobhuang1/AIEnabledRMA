using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIEnabledRma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTriageRefusals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "triage_refusals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    statement = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_triage_refusals", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_triage_refusals_at",
                table: "triage_refusals",
                column: "at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_triage_refusals_reason_at",
                table: "triage_refusals",
                columns: new[] { "reason", "at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "triage_refusals");
        }
    }
}
