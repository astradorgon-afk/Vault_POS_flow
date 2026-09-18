using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SyncChangeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_log",
                schema: "sync",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    change_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    payload_json = table.Column<string>(type: "character varying(524288)", maxLength: 524288, nullable: false),
                    location_scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_log", x => x.sequence);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_log_scope_sequence",
                schema: "sync",
                table: "change_log",
                columns: new[] { "location_scope_id", "sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_log",
                schema: "sync");
        }
    }
}
