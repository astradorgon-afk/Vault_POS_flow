using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceSaleCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_vat_exempt",
                table: "cache_product",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "cache_batch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    lot_number = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    received_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    unit_cost = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_batch", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cache_batch_fefo",
                table: "cache_batch",
                columns: new[] { "product_id", "expires_on" });

            // cache_batch is applier-owned like every other downloaded table, so
            // it carries the same guards (C29): only the change-feed applier may
            // write it, and a connection without the guard function fails closed.
            foreach (string suffix in new[] { "insert", "update", "delete" })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER trg_cache_batch_feed_only_{suffix}
                    BEFORE {suffix.ToUpperInvariant()} ON cache_batch
                    WHEN vf_change_feed_writer() IS NOT 1
                    BEGIN
                        SELECT RAISE(ABORT, 'cache_batch is written only by the change-feed applier');
                    END;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (string suffix in new[] { "insert", "update", "delete" })
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_cache_batch_feed_only_{suffix};");
            }

            migrationBuilder.DropTable(
                name: "cache_batch");

            migrationBuilder.DropColumn(
                name: "is_vat_exempt",
                table: "cache_product");
        }
    }
}
