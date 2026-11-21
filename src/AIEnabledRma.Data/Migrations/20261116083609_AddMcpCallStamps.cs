using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIEnabledRma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpCallStamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_call_stamps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    caller = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_call_stamps", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_call_stamps_caller_kind_at",
                table: "mcp_call_stamps",
                columns: new[] { "caller", "kind", "at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_call_stamps");
        }
    }
}
