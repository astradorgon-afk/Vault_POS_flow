using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SyncChangeFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_feed",
                schema: "sync",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    location_scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_json = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_feed", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "feed_sequence",
                schema: "sync",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feed_sequence", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_feed_scope_sequence",
                schema: "sync",
                table: "change_feed",
                columns: new[] { "location_scope_id", "sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_feed",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "feed_sequence",
                schema: "sync");
        }
    }
}
