using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class WarehouseSupplyRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supply_request",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supply_request", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "supply_request_line",
                schema: "purchasing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ordered_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supply_request_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_supply_request_line_supply_request_supply_request_id",
                        column: x => x.supply_request_id,
                        principalSchema: "purchasing",
                        principalTable: "supply_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supply_request_order",
                schema: "purchasing",
                columns: table => new
                {
                    supply_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supply_request_order", x => new { x.supply_request_id, x.purchase_order_id });
                    table.ForeignKey(
                        name: "FK_supply_request_order_supply_request_supply_request_id",
                        column: x => x.supply_request_id,
                        principalSchema: "purchasing",
                        principalTable: "supply_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_supply_request_status_requested",
                schema: "purchasing",
                table: "supply_request",
                columns: new[] { "status", "requested_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_supply_request_number",
                schema: "purchasing",
                table: "supply_request",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_supply_request_line_no",
                schema: "purchasing",
                table: "supply_request_line",
                columns: new[] { "supply_request_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supply_request_order_purchase_order",
                schema: "purchasing",
                table: "supply_request_order",
                column: "purchase_order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supply_request_line",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "supply_request_order",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "supply_request",
                schema: "purchasing");
        }
    }
}
