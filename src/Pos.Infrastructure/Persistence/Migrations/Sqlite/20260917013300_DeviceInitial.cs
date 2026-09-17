using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cache_location",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    kind = table.Column<short>(type: "INTEGER", nullable: false),
                    time_zone_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    currency_code = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_location", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cache_product",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    tracks_batches = table.Column<bool>(type: "INTEGER", nullable: false),
                    tracks_expiry = table.Column<bool>(type: "INTEGER", nullable: false),
                    source_version = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_product", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cache_product_barcode",
                columns: table => new
                {
                    barcode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    is_primary = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_product_barcode", x => x.barcode);
                });

            migrationBuilder.CreateTable(
                name: "cache_product_price",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    amount = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    effective_from_utc = table.Column<string>(type: "TEXT", nullable: false),
                    effective_to_utc = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_product_price", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cache_user",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    security_version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_profile",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    short_code = table.Column<string>(type: "TEXT", maxLength: 6, nullable: false),
                    enrolled_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_profile", x => x.device_id);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_permission",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    permission = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    policy_version = table.Column<long>(type: "INTEGER", nullable: false),
                    issued_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshot_permission", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_cache_location_code",
                table: "cache_location",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_cache_product_sku",
                table: "cache_product",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cache_product_barcode_product",
                table: "cache_product_barcode",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_cache_product_price_effective",
                table: "cache_product_price",
                columns: new[] { "product_id", "location_id", "effective_from_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_cache_user_name",
                table: "cache_user",
                column: "user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_device_profile_short_code",
                table: "device_profile",
                column: "short_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_permission_expiry",
                table: "snapshot_permission",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_snapshot_permission_global",
                table: "snapshot_permission",
                columns: new[] { "user_id", "permission" },
                unique: true,
                filter: "location_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_snapshot_permission_location",
                table: "snapshot_permission",
                columns: new[] { "user_id", "permission", "location_id" },
                unique: true,
                filter: "location_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cache_location");

            migrationBuilder.DropTable(
                name: "cache_product");

            migrationBuilder.DropTable(
                name: "cache_product_barcode");

            migrationBuilder.DropTable(
                name: "cache_product_price");

            migrationBuilder.DropTable(
                name: "cache_user");

            migrationBuilder.DropTable(
                name: "device_profile");

            migrationBuilder.DropTable(
                name: "snapshot_permission");
        }
    }
}
