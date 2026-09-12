using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "transfers");

            migrationBuilder.CreateTable(
                name: "transfer_order",
                schema: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    picked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    picked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dispatched_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shipment_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receipt_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    received_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_order", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transfer_allocation",
                schema: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    received_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    damaged_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_allocation", x => x.id);
                    table.ForeignKey(
                        name: "FK_transfer_allocation_transfer_order_transfer_order_id",
                        column: x => x.transfer_order_id,
                        principalSchema: "transfers",
                        principalTable: "transfer_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transfer_custody_event",
                schema: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_custody_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_transfer_custody_event_transfer_order_transfer_order_id",
                        column: x => x.transfer_order_id,
                        principalSchema: "transfers",
                        principalTable: "transfer_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transfer_discrepancy",
                schema: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    resolution_outcome = table.Column<short>(type: "smallint", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_discrepancy", x => x.id);
                    table.ForeignKey(
                        name: "FK_transfer_discrepancy_transfer_order_transfer_order_id",
                        column: x => x.transfer_order_id,
                        principalSchema: "transfers",
                        principalTable: "transfer_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transfer_order_line",
                schema: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_order_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_transfer_order_line_transfer_order_transfer_order_id",
                        column: x => x.transfer_order_id,
                        principalSchema: "transfers",
                        principalTable: "transfer_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transfer_allocation_order_line",
                schema: "transfers",
                table: "transfer_allocation",
                columns: new[] { "transfer_order_id", "line_no" });

            migrationBuilder.CreateIndex(
                name: "ux_transfer_custody_sequence",
                schema: "transfers",
                table: "transfer_custody_event",
                columns: new[] { "transfer_order_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transfer_discrepancy_order_line",
                schema: "transfers",
                table: "transfer_discrepancy",
                columns: new[] { "transfer_order_id", "line_no" });

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_destination_time",
                schema: "transfers",
                table: "transfer_order",
                columns: new[] { "destination_location_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_source_time",
                schema: "transfers",
                table: "transfer_order",
                columns: new[] { "source_location_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_transfer_order_number",
                schema: "transfers",
                table: "transfer_order",
                column: "number",
                unique: true,
                filter: "\"number\" <> ''");

            migrationBuilder.CreateIndex(
                name: "ux_transfer_order_line_no",
                schema: "transfers",
                table: "transfer_order_line",
                columns: new[] { "transfer_order_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transfer_allocation",
                schema: "transfers");

            migrationBuilder.DropTable(
                name: "transfer_custody_event",
                schema: "transfers");

            migrationBuilder.DropTable(
                name: "transfer_discrepancy",
                schema: "transfers");

            migrationBuilder.DropTable(
                name: "transfer_order_line",
                schema: "transfers");

            migrationBuilder.DropTable(
                name: "transfer_order",
                schema: "transfers");
        }
    }
}
