using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceLocalSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Tin = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeactivatedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    DeactivationReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "local_sale",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    status = table.Column<short>(type: "INTEGER", nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cashier_shift_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    customer_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    business_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    completed_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    completed_by_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    voided_at_utc = table.Column<string>(type: "TEXT", nullable: true),
                    voided_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    void_reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    gross_total = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    discount_total = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    net_total = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    vat_total = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    vat_exempt_total = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    zero_rated_total = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    taxable_base_total = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_sale", x => x.id);
                    table.ForeignKey(
                        name: "FK_local_sale_Customer_customer_id",
                        column: x => x.customer_id,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "local_payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    method = table.Column<short>(type: "INTEGER", nullable: false),
                    amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    tendered = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    change_given = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    provider_reference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    sale_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_payment", x => x.id);
                    table.ForeignKey(
                        name: "FK_local_payment_local_sale_sale_id",
                        column: x => x.sale_id,
                        principalTable: "local_sale",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "local_sale_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sale_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    line_no = table.Column<int>(type: "INTEGER", nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    product_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    barcode = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    quantity = table.Column<string>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    uom_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    unit_price = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    price_version = table.Column<Guid>(type: "TEXT", nullable: false),
                    price_was_overridden = table.Column<bool>(type: "INTEGER", nullable: false),
                    price_override_authorized_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    discount_amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    discount_authorized_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    vat_rate = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    is_vat_exempt = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_zero_rated = table.Column<bool>(type: "INTEGER", nullable: false),
                    vat_base = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    vat_amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    batch_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    batch_code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    batch_expires_on = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    unit_cost = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    gross_amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    net_amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    returned_quantity = table.Column<string>(type: "TEXT", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_sale_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_local_sale_item_local_sale_sale_id",
                        column: x => x.sale_id,
                        principalTable: "local_sale",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_sale",
                table: "local_payment",
                columns: new[] { "sale_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_local_sale_customer_id",
                table: "local_sale",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_sale_location_business_date",
                table: "local_sale",
                columns: new[] { "location_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_shift",
                table: "local_sale",
                columns: new[] { "cashier_shift_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_sale_event_id",
                table: "local_sale",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sale_number",
                table: "local_sale",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sale_item_product",
                table: "local_sale_item",
                columns: new[] { "product_id", "sale_id" });

            migrationBuilder.CreateIndex(
                name: "ux_sale_item_line_no",
                table: "local_sale_item",
                columns: new[] { "sale_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_payment");

            migrationBuilder.DropTable(
                name: "local_sale_item");

            migrationBuilder.DropTable(
                name: "local_sale");

            migrationBuilder.DropTable(
                name: "Customer");
        }
    }
}
