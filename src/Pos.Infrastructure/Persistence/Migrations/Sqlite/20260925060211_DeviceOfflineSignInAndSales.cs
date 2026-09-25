using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceOfflineSignInAndSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "base_unit_of_measure_id",
                table: "cache_product",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "local_offline_credential",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    login_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    salt = table.Column<byte[]>(type: "BLOB", nullable: false),
                    verifier = table.Column<byte[]>(type: "BLOB", nullable: false),
                    iterations = table.Column<int>(type: "INTEGER", nullable: false),
                    recorded_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    failed_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    locked_until_utc = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_offline_credential", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "local_sale",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    shift_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cashier_name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    customer_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    customer_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    business_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    completed_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    net_total = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    lines_json = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: false),
                    payments_json = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_sale", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_local_offline_credential_login_name",
                table: "local_offline_credential",
                column: "login_name");

            migrationBuilder.CreateIndex(
                name: "ux_local_offline_credential_user_name",
                table: "local_offline_credential",
                column: "user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_local_sale_shift",
                table: "local_sale",
                column: "shift_id");

            migrationBuilder.CreateIndex(
                name: "ux_local_sale_number",
                table: "local_sale",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_offline_credential");

            migrationBuilder.DropTable(
                name: "local_sale");

            migrationBuilder.DropColumn(
                name: "base_unit_of_measure_id",
                table: "cache_product");
        }
    }
}
