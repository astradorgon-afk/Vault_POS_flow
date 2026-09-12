using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class PurchasingCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "purchasing");

            migrationBuilder.CreateTable(
                name: "document_counter",
                schema: "core",
                columns: table => new
                {
                    document_type = table.Column<short>(type: "smallint", nullable: false),
                    period_key = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    scope_key = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_counter", x => new { x.document_type, x.period_key, x.scope_key });
                });

            migrationBuilder.CreateTable(
                name: "purchase_order",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    ordered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    closed_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_order", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "purchase_approval",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approver_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<short>(type: "smallint", nullable: false),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    threshold_applied = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_approval", x => x.id);
                    table.ForeignKey(
                        name: "FK_purchase_approval_purchase_order_purchase_order_id",
                        column: x => x.purchase_order_id,
                        principalSchema: "purchasing",
                        principalTable: "purchase_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "purchase_order_line",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordered_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_order_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_purchase_order_line_purchase_order_purchase_order_id",
                        column: x => x.purchase_order_id,
                        principalSchema: "purchasing",
                        principalTable: "purchase_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_approval_order_time",
                schema: "purchasing",
                table: "purchase_approval",
                columns: new[] { "purchase_order_id", "decided_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_purchase_order_number",
                schema: "purchasing",
                table: "purchase_order",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_purchase_order_line_no",
                schema: "purchasing",
                table: "purchase_order_line",
                columns: new[] { "purchase_order_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_counter",
                schema: "core");

            migrationBuilder.DropTable(
                name: "purchase_approval",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "purchase_order_line",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "purchase_order",
                schema: "purchasing");
        }
    }
}
