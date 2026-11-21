using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIEnabledRma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpToolCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_tool_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    caller = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    argument_hashes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    result_count = table.Column<int>(type: "integer", nullable: false),
                    latency_ms = table.Column<long>(type: "bigint", nullable: false),
                    refused = table.Column<bool>(type: "boolean", nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_tool_calls", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_tool_calls_at",
                table: "mcp_tool_calls",
                column: "at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_tool_calls_tool_at",
                table: "mcp_tool_calls",
                columns: new[] { "tool", "at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_tool_calls");
        }
    }
}
