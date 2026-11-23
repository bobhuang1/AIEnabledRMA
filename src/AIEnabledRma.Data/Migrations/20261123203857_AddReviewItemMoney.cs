using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIEnabledRma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewItemMoney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "expected_amount",
                table: "review_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "expected_currency_code",
                table: "review_items",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "provider_amount",
                table: "review_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_currency_code",
                table: "review_items",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expected_amount",
                table: "review_items");

            migrationBuilder.DropColumn(
                name: "expected_currency_code",
                table: "review_items");

            migrationBuilder.DropColumn(
                name: "provider_amount",
                table: "review_items");

            migrationBuilder.DropColumn(
                name: "provider_currency_code",
                table: "review_items");
        }
    }
}
