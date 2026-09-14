using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddShift : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cashier_shift",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opening_float = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    opened_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    closed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    declared_cash = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    counted_cash = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    cash_variance = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cashier_shift", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cashier_shift_device",
                schema: "sales",
                table: "cashier_shift",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_shift_location_business_date",
                schema: "sales",
                table: "cashier_shift",
                columns: new[] { "location_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ux_cashier_shift_number",
                schema: "sales",
                table: "cashier_shift",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cashier_shift",
                schema: "sales");
        }
    }
}
