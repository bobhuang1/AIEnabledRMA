using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIEnabledRma.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_customer_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    first_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    middle_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    company_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    secondary_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    phone_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    preferred_language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    tax_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    email_opt_in = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_merchandise = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "addresses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    line1 = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    line2 = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    line3 = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    city = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    state_or_province = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    phone_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_addresses", x => x.id);
                    table.ForeignKey(
                        name: "fk_addresses_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    mac_address = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    imei = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    hardware_revision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    firmware_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    replacement_for_serial_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    shipped_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_devices_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rma_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rma_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ship_to_address_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    warranty_tier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    currency_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    shipping_charge = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    deposit_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    payment_transaction_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    eligibility_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    policy_rule_trace = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    troubleshooting_summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rma_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_rma_requests_addresses_ship_to_address_id",
                        column: x => x.ship_to_address_id,
                        principalTable: "addresses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_rma_requests_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "device_alternate_identifiers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_alternate_identifiers", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_alternate_identifiers_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "warranties",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    tier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warranties", x => x.id);
                    table.ForeignKey(
                        name: "fk_warranties_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rma_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rma_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    problem_category_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    problem_description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    what_customer_tried = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    recommended_steps_json = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    triage_verdict = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    triage_confidence = table.Column<double>(type: "double precision", precision: 4, scale: 3, nullable: true),
                    triage_article_ids = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rma_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_rma_lines_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_rma_lines_rma_requests_rma_request_id",
                        column: x => x.rma_request_id,
                        principalTable: "rma_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_addresses_customer",
                table: "addresses",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_customers_email",
                table: "customers",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "ix_customers_external_id",
                table: "customers",
                column: "external_customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_alt_identifier",
                table: "device_alternate_identifiers",
                column: "identifier");

            migrationBuilder.CreateIndex(
                name: "ix_device_alternate_identifiers_device_id",
                table: "device_alternate_identifiers",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_devices_product_id",
                table: "devices",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_devices_serial",
                table: "devices",
                column: "serial_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_products_sku",
                table: "products",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rma_lines_device",
                table: "rma_lines",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_rma_lines_rma_request_id",
                table: "rma_lines",
                column: "rma_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_rma_requests_customer_id",
                table: "rma_requests",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_rma_requests_ship_to_address_id",
                table: "rma_requests",
                column: "ship_to_address_id");

            migrationBuilder.CreateIndex(
                name: "ix_rma_status_created",
                table: "rma_requests",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_rma_requests_number",
                table: "rma_requests",
                column: "rma_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_warranty_device_end",
                table: "warranties",
                columns: new[] { "device_id", "end_date" });

            // ---------------------------------------------------------------------
            // Hand-added below this point. Everything above is EF-scaffolded; the raw SQL
            // here exists because these objects have no EF equivalent:
            //
            //   pg_trgm supplies similarity() and GIN-accelerated LIKE '%term%', which is
            //   what lets "josephin smyth" match "Josephine Smith" without a table scan.
            //   The expression indexes are on concatenated columns because the value the
            //   repositories actually search is the concatenation
            //   ("first_name || ' ' || last_name"), and a plain btree index cannot serve
            //   that. EF's LINQ translates c.FirstName + " " + c.LastName to exactly this
            //   expression, so the index and the query line up.
            //
            // These are deliberately NOT modelled in RmaDbContext: the repositories issue
            // the fuzzy prefilter through LINQ, so recording the indexes in the model would
            // make EF create a second, wrong version of each one. The two case-insensitive
            // UNIQUE indexes are also raw SQL because EF cannot translate lower()/upper()
            // inside an index expression at design time.
            // ---------------------------------------------------------------------

            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql("""
                CREATE INDEX ix_customers_name_trgm
                    ON customers USING gin ((first_name || ' ' || last_name) gin_trgm_ops);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_customers_email_trgm
                    ON customers USING gin (email gin_trgm_ops);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_customers_phone_trgm
                    ON customers USING gin (phone_number gin_trgm_ops);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_addresses_trgm
                    ON addresses USING gin (
                        (line1 || ' ' || city || ' ' || coalesce(state_or_province, '')) gin_trgm_ops);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_device_alt_identifier_trgm
                    ON device_alternate_identifiers USING gin (identifier gin_trgm_ops);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_rma_lines_description_trgm
                    ON rma_lines USING gin (problem_description gin_trgm_ops);
                """);

            // The real uniqueness constraints. The scaffolded ux_devices_serial and
            // ux_products_sku above are case-sensitive and would accept "sn-abc" alongside
            // "SN-ABC" as two distinct units; serial numbers are transcribed by hand from a
            // label, so they must compare case-insensitively.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_devices_serial_upper
                    ON devices (upper(serial_number));
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_products_sku_lower
                    ON products (lower(sku));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Expression indexes are dropped explicitly first: they are owned by a table that
            // is about to go away, and letting the DROP TABLE cascade handle them would work
            // but would hide which objects this migration actually created.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_products_sku_lower;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_devices_serial_upper;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_rma_lines_description_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_device_alt_identifier_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_addresses_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_customers_phone_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_customers_email_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_customers_name_trgm;");

            migrationBuilder.DropTable(
                name: "device_alternate_identifiers");

            migrationBuilder.DropTable(
                name: "rma_lines");

            migrationBuilder.DropTable(
                name: "warranties");

            migrationBuilder.DropTable(
                name: "rma_requests");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "addresses");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "customers");
        }
    }
}
