using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceChangeFeedGuards : Migration
    {
        // Frozen with this migration: a later table joins the guard in its own migration.
        private static readonly string[] ChangeFeedOwnedTables =
        [
            "cache_location",
            "cache_product",
            "cache_product_barcode",
            "cache_product_price",
            "cache_user",
            "snapshot_permission",
            "sync_cursor",
        ];

        private static readonly (string Statement, string Suffix)[] GuardedOperations =
        [
            ("INSERT", "insert"),
            ("UPDATE", "update"),
            ("DELETE", "delete"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_cursor",
                columns: table => new
                {
                    feed = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    position = table.Column<long>(type: "INTEGER", nullable: false),
                    advanced_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_cursor", x => x.feed);
                });

            // The function is registered per connection by the device context and
            // returns 1 only inside the change-feed applier's write scope. On a
            // connection without it the trigger cannot run, so the write fails.
            foreach (string table in ChangeFeedOwnedTables)
            {
                foreach ((string statement, string suffix) in GuardedOperations)
                {
                    migrationBuilder.Sql($"""
                        CREATE TRIGGER trg_{table}_feed_only_{suffix}
                        BEFORE {statement} ON {table}
                        WHEN vf_change_feed_writer() IS NOT 1
                        BEGIN
                            SELECT RAISE(ABORT, '{table} is written only by the change-feed applier');
                        END;
                        """);
                }
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (string table in ChangeFeedOwnedTables)
            {
                foreach ((_, string suffix) in GuardedOperations)
                {
                    migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_{table}_feed_only_{suffix};");
                }
            }

            migrationBuilder.DropTable(
                name: "sync_cursor");
        }
    }
}
