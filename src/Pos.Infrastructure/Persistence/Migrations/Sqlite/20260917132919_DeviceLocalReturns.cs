using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceLocalReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_sales_return",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sale_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    is_blind = table.Column<bool>(type: "INTEGER", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cashier_shift_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    customer_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    business_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    returned_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    returned_by_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    refundable_total = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_sales_return", x => x.id);
                    table.ForeignKey(
                        name: "FK_local_sales_return_Customer_customer_id",
                        column: x => x.customer_id,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "local_refund",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sales_return_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cashier_shift_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    method = table.Column<short>(type: "INTEGER", nullable: false),
                    amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    tendered = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    provider_reference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    refunded_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    refunded_by_user_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_refund", x => x.id);
                    table.ForeignKey(
                        name: "FK_local_refund_local_sales_return_sales_return_id",
                        column: x => x.sales_return_id,
                        principalTable: "local_sales_return",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "local_sales_return_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sales_return_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    line_no = table.Column<int>(type: "INTEGER", nullable: false),
                    sale_item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    product_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    barcode = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    quantity = table.Column<string>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    dispositioned_quantity = table.Column<string>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    uom_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    unit_price = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    gross_amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    discount_amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    net_amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    refundable_amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    vat_rate = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    is_vat_exempt = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_zero_rated = table.Column<bool>(type: "INTEGER", nullable: false),
                    vat_base = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    vat_amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    batch_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    batch_code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    batch_expires_on = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    unit_cost = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_sales_return_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_local_sales_return_item_local_sales_return_sales_return_id",
                        column: x => x.sales_return_id,
                        principalTable: "local_sales_return",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refund_return",
                table: "local_refund",
                columns: new[] { "sales_return_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_refund_shift_method",
                table: "local_refund",
                columns: new[] { "cashier_shift_id", "method" });

            migrationBuilder.CreateIndex(
                name: "ux_refund_event_id",
                table: "local_refund",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_sales_return_customer_id",
                table: "local_sales_return",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_location_business_date",
                table: "local_sales_return",
                columns: new[] { "location_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_sale",
                table: "local_sales_return",
                columns: new[] { "sale_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_return_event_id",
                table: "local_sales_return",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sales_return_number",
                table: "local_sales_return",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_item_product",
                table: "local_sales_return_item",
                columns: new[] { "product_id", "sales_return_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_item_sale_item",
                table: "local_sales_return_item",
                column: "sale_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_sales_return_item_line_no",
                table: "local_sales_return_item",
                columns: new[] { "sales_return_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_refund");

            migrationBuilder.DropTable(
                name: "local_sales_return_item");

            migrationBuilder.DropTable(
                name: "local_sales_return");
        }
    }
}
