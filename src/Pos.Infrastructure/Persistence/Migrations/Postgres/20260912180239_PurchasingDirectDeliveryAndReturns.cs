using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class PurchasingDirectDeliveryAndReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "resolution_note",
                schema: "purchasing",
                table: "receiving_discrepancy",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "resolution_outcome",
                schema: "purchasing",
                table: "receiving_discrepancy",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resolved_at_utc",
                schema: "purchasing",
                table: "receiving_discrepancy",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "resolved_by_user_id",
                schema: "purchasing",
                table: "receiving_discrepancy",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "direct_delivery_authorization",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    value_cap = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_direct_delivery_authorization", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "supplier_return",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    supplier_authorization_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_return", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "supplier_return_line",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_state = table.Column<short>(type: "smallint", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    reason = table.Column<short>(type: "smallint", nullable: false),
                    notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_return_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_supplier_return_line_supplier_return_supplier_return_id",
                        column: x => x.supplier_return_id,
                        principalSchema: "purchasing",
                        principalTable: "supplier_return",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dda_supplier_store_window",
                schema: "purchasing",
                table: "direct_delivery_authorization",
                columns: new[] { "supplier_id", "store_location_id", "valid_from", "valid_until" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_return_location_time",
                schema: "purchasing",
                table: "supplier_return",
                columns: new[] { "location_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_supplier_return_number",
                schema: "purchasing",
                table: "supplier_return",
                column: "number",
                unique: true,
                filter: "\"number\" <> ''");

            migrationBuilder.CreateIndex(
                name: "ux_supplier_return_line_no",
                schema: "purchasing",
                table: "supplier_return_line",
                columns: new[] { "supplier_return_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "direct_delivery_authorization",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "supplier_return_line",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "supplier_return",
                schema: "purchasing");

            migrationBuilder.DropColumn(
                name: "resolution_note",
                schema: "purchasing",
                table: "receiving_discrepancy");

            migrationBuilder.DropColumn(
                name: "resolution_outcome",
                schema: "purchasing",
                table: "receiving_discrepancy");

            migrationBuilder.DropColumn(
                name: "resolved_at_utc",
                schema: "purchasing",
                table: "receiving_discrepancy");

            migrationBuilder.DropColumn(
                name: "resolved_by_user_id",
                schema: "purchasing",
                table: "receiving_discrepancy");
        }
    }
}
