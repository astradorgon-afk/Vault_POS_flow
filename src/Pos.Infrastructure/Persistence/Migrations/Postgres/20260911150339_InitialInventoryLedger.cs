using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InitialInventoryLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "inventory_balance",
                schema: "inventory",
                columns: table => new
                {
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_key = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    average_unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    total_value = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    last_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_movement_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_balance", x => new { x.location_id, x.product_id, x.batch_key, x.state });
                });

            migrationBuilder.CreateTable(
                name: "inventory_movement",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leg_number = table.Column<short>(type: "smallint", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    quantity_delta = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    total_value_delta = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    movement_type = table.Column<short>(type: "smallint", nullable: false),
                    source_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_state = table.Column<short>(type: "smallint", nullable: true),
                    destination_state = table.Column<short>(type: "smallint", nullable: true),
                    reference_document_type = table.Column<short>(type: "smallint", nullable: false),
                    reference_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason_code = table.Column<short>(type: "smallint", nullable: true),
                    notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    reverses_movement_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sync_status = table.Column<short>(type: "smallint", nullable: false),
                    server_processing_status = table.Column<short>(type: "smallint", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_movement", x => x.id);
                    table.CheckConstraint("ck_inventory_movement_cost_nonnegative", "unit_cost >= 0");
                    table.CheckConstraint("ck_inventory_movement_leg_positive", "leg_number >= 1");
                    table.CheckConstraint("ck_inventory_movement_quantity_nonzero", "quantity_delta <> 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_balance_location_state",
                schema: "inventory",
                table: "inventory_balance",
                columns: new[] { "location_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_balance_product_location",
                schema: "inventory",
                table: "inventory_balance",
                columns: new[] { "product_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_bucket",
                schema: "inventory",
                table: "inventory_movement",
                columns: new[] { "location_id", "product_id", "batch_id", "state", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_event",
                schema: "inventory",
                table: "inventory_movement",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_group",
                schema: "inventory",
                table: "inventory_movement",
                column: "movement_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_product_time",
                schema: "inventory",
                table: "inventory_movement",
                columns: new[] { "product_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_reference",
                schema: "inventory",
                table: "inventory_movement",
                columns: new[] { "reference_document_type", "reference_document_id" });

            migrationBuilder.CreateIndex(
                name: "ux_inventory_movement_group_leg",
                schema: "inventory",
                table: "inventory_movement",
                columns: new[] { "movement_group_id", "leg_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_balance",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "inventory_movement",
                schema: "inventory");
        }
    }
}
