using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InventoryControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventory_count",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    snapshot_taken_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_rejection_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_count", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_adjustment",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<short>(type: "smallint", nullable: false),
                    notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    reversed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reversed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reversal_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_adjustment", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inventory_count_line",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_count_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    system_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    physical_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    counted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    counted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_repeat_variance = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_count_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_inventory_count_line_inventory_count_inventory_count_id",
                        column: x => x.inventory_count_id,
                        principalSchema: "inventory",
                        principalTable: "inventory_count",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_adjustment_line",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_adjustment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    quantity_delta = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    movement_type = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_adjustment_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_adjustment_line_stock_adjustment_stock_adjustment_id",
                        column: x => x.stock_adjustment_id,
                        principalSchema: "inventory",
                        principalTable: "stock_adjustment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_count_location_time",
                schema: "inventory",
                table: "inventory_count",
                columns: new[] { "location_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_inventory_count_number",
                schema: "inventory",
                table: "inventory_count",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inventory_count_line_product",
                schema: "inventory",
                table: "inventory_count_line",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_inventory_count_line_no",
                schema: "inventory",
                table: "inventory_count_line",
                columns: new[] { "inventory_count_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_adjustment_location_time",
                schema: "inventory",
                table: "stock_adjustment",
                columns: new[] { "location_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_adjustment_number",
                schema: "inventory",
                table: "stock_adjustment",
                column: "number",
                unique: true,
                filter: "\"number\" <> ''");

            migrationBuilder.CreateIndex(
                name: "ux_stock_adjustment_line_no",
                schema: "inventory",
                table: "stock_adjustment_line",
                columns: new[] { "stock_adjustment_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_count_line",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stock_adjustment_line",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "inventory_count",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stock_adjustment",
                schema: "inventory");
        }
    }
}
