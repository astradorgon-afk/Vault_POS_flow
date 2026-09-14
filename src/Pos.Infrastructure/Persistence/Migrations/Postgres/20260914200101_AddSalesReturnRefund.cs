using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSalesReturnRefund : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "returned_quantity",
                schema: "sales",
                table: "sale_item",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "sales_return",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    returned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    returned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refundable_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_return", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refund",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<short>(type: "smallint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tendered = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    refunded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    refunded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refund", x => x.id);
                    table.ForeignKey(
                        name: "FK_refund_sales_return_sales_return_id",
                        column: x => x.sales_return_id,
                        principalSchema: "sales",
                        principalTable: "sales_return",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sales_return_item",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    sale_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    barcode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    uom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    gross_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    refundable_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    vat_rate = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    is_vat_exempt = table.Column<bool>(type: "boolean", nullable: false),
                    is_zero_rated = table.Column<bool>(type: "boolean", nullable: false),
                    vat_base = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    vat_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    batch_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    batch_expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_return_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_sales_return_item_sales_return_sales_return_id",
                        column: x => x.sales_return_id,
                        principalSchema: "sales",
                        principalTable: "sales_return",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refund_return",
                schema: "sales",
                table: "refund",
                columns: new[] { "sales_return_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_refund_shift_method",
                schema: "sales",
                table: "refund",
                columns: new[] { "cashier_shift_id", "method" });

            migrationBuilder.CreateIndex(
                name: "ux_refund_event_id",
                schema: "sales",
                table: "refund",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_location_business_date",
                schema: "sales",
                table: "sales_return",
                columns: new[] { "location_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_sale",
                schema: "sales",
                table: "sales_return",
                columns: new[] { "sale_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_return_event_id",
                schema: "sales",
                table: "sales_return",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sales_return_number",
                schema: "sales",
                table: "sales_return",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_item_product",
                schema: "sales",
                table: "sales_return_item",
                columns: new[] { "product_id", "sales_return_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_item_sale_item",
                schema: "sales",
                table: "sales_return_item",
                column: "sale_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_sales_return_item_line_no",
                schema: "sales",
                table: "sales_return_item",
                columns: new[] { "sales_return_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refund",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "sales_return_item",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "sales_return",
                schema: "sales");

            migrationBuilder.DropColumn(
                name: "returned_quantity",
                schema: "sales",
                table: "sale_item");
        }
    }
}
