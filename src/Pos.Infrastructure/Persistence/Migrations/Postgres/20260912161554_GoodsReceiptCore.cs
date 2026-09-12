using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class GoodsReceiptCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "batch",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_on = table.Column<DateOnly>(type: "date", nullable: false),
                    manufactured_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batch", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "goods_receipt",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    documents_missing = table.Column<bool>(type: "boolean", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cost_variance_pending_approval = table.Column<bool>(type: "boolean", nullable: false),
                    cost_variance_value_at_stake = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goods_receipt", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "goods_receipt_line",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    goods_receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    purchase_order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity_expected = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    quantity_received = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    quantity_damaged = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    quantity_wrong_item = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    quantity_expired = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    overage_beyond_tolerance = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    quantity_accepted = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    accepted_state = table.Column<short>(type: "smallint", nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    lot_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    manufactured_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    cost_variance_percent = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    cost_variance_approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cost_variance_approved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goods_receipt_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_goods_receipt_line_goods_receipt_goods_receipt_id",
                        column: x => x.goods_receipt_id,
                        principalSchema: "purchasing",
                        principalTable: "goods_receipt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "receiving_discrepancy",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    goods_receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    value_impact = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receiving_discrepancy", x => x.id);
                    table.ForeignKey(
                        name: "FK_receiving_discrepancy_goods_receipt_goods_receipt_id",
                        column: x => x.goods_receipt_id,
                        principalSchema: "purchasing",
                        principalTable: "goods_receipt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_batch_product_lot",
                schema: "inventory",
                table: "batch",
                columns: new[] { "product_id", "lot_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_goods_receipt_order_time",
                schema: "purchasing",
                table: "goods_receipt",
                columns: new[] { "purchase_order_id", "received_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_goods_receipt_number",
                schema: "purchasing",
                table: "goods_receipt",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_goods_receipt_line_no",
                schema: "purchasing",
                table: "goods_receipt_line",
                columns: new[] { "goods_receipt_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receiving_discrepancy_goods_receipt_id",
                schema: "purchasing",
                table: "receiving_discrepancy",
                column: "goods_receipt_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "batch",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "goods_receipt_line",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "receiving_discrepancy",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "goods_receipt",
                schema: "purchasing");
        }
    }
}
