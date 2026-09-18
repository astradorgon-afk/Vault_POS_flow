using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SyncEventInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sync");

            migrationBuilder.CreateTable(
                name: "device_checkpoint",
                schema: "sync",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_accepted_sequence = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_checkpoint", x => x.device_id);
                });

            migrationBuilder.CreateTable(
                name: "processed_event",
                schema: "sync",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_sequence = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload_json = table.Column<string>(type: "character varying(524288)", maxLength: 524288, nullable: false),
                    payload_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_uptime_ticks = table.Column<long>(type: "bigint", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    applied_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    response_json = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_event", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_processed_event_device_time",
                schema: "sync",
                table: "processed_event",
                columns: new[] { "device_id", "applied_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_processed_event_device_sequence",
                schema: "sync",
                table: "processed_event",
                columns: new[] { "device_id", "device_sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_checkpoint",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "processed_event",
                schema: "sync");
        }
    }
}
