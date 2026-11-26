using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIEnabledRma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpToolCallReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "mcp_tool_calls",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reason",
                table: "mcp_tool_calls");
        }
    }
}
