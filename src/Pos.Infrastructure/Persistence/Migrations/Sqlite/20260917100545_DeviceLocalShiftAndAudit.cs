using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceLocalShiftAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "settings_json",
                table: "cache_location",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "local_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    previous_value_json = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    new_value_json = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    recorded_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "local_cashier_shift",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    opening_float = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    business_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    opened_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<short>(type: "INTEGER", nullable: false),
                    closed_at_utc = table.Column<string>(type: "TEXT", nullable: true),
                    declared_cash = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    counted_cash = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    cash_variance = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    is_force_closed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_cashier_shift", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_local_audit_recorded",
                table: "local_audit",
                column: "recorded_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_local_cashier_shift_status",
                table: "local_cashier_shift",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_local_cashier_shift_number",
                table: "local_cashier_shift",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_audit");

            migrationBuilder.DropTable(
                name: "local_cashier_shift");

            migrationBuilder.DropColumn(
                name: "settings_json",
                table: "cache_location");
        }
    }
}
