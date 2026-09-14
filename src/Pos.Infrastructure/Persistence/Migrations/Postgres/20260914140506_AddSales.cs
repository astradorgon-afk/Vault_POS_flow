using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sales");

            migrationBuilder.CreateTable(
                name: "sale",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gross_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    net_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    vat_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    vat_exempt_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    zero_rated_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    taxable_base_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<short>(type: "smallint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tendered = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    change_given = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment", x => x.id);
                    table.ForeignKey(
                        name: "FK_payment_sale_sale_id",
                        column: x => x.sale_id,
                        principalSchema: "sales",
                        principalTable: "sale",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sale_item",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    barcode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    uom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    price_version = table.Column<Guid>(type: "uuid", nullable: false),
                    price_was_overridden = table.Column<bool>(type: "boolean", nullable: false),
                    price_override_authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    discount_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vat_rate = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    is_vat_exempt = table.Column<bool>(type: "boolean", nullable: false),
                    is_zero_rated = table.Column<bool>(type: "boolean", nullable: false),
                    vat_base = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    vat_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    batch_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    batch_expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    gross_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_sale_item_sale_sale_id",
                        column: x => x.sale_id,
                        principalSchema: "sales",
                        principalTable: "sale",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_sale",
                schema: "sales",
                table: "payment",
                columns: new[] { "sale_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_location_business_date",
                schema: "sales",
                table: "sale",
                columns: new[] { "location_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_shift",
                schema: "sales",
                table: "sale",
                columns: new[] { "cashier_shift_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_sale_event_id",
                schema: "sales",
                table: "sale",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sale_number",
                schema: "sales",
                table: "sale",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sale_item_product",
                schema: "sales",
                table: "sale_item",
                columns: new[] { "product_id", "sale_id" });

            migrationBuilder.CreateIndex(
                name: "ux_sale_item_line_no",
                schema: "sales",
                table: "sale_item",
                columns: new[] { "sale_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "sale_item",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "sale",
                schema: "sales");
        }
    }
}
