using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SupplyRequestOrderQuantities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "released_at_utc",
                schema: "purchasing",
                table: "supply_request_order",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "supply_request_order_line",
                schema: "purchasing",
                columns: table => new
                {
                    supply_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supply_request_order_line", x => new { x.supply_request_id, x.purchase_order_id, x.line_no });
                    table.ForeignKey(
                        name: "FK_supply_request_order_line_supply_request_order_supply_reque~",
                        columns: x => new { x.supply_request_id, x.purchase_order_id },
                        principalSchema: "purchasing",
                        principalTable: "supply_request_order",
                        principalColumns: new[] { "supply_request_id", "purchase_order_id" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supply_request_order_line",
                schema: "purchasing");

            migrationBuilder.DropColumn(
                name: "released_at_utc",
                schema: "purchasing",
                table: "supply_request_order");
        }
    }
}
