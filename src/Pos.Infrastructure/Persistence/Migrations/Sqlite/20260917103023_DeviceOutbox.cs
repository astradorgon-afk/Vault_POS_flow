using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_sequence",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    next_value = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_sequence", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "local_outbox_event",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    type = table.Column<short>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: false),
                    payload_hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    occurred_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    device_uptime_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    correlation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<short>(type: "INTEGER", nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_attempt_at_utc = table.Column<string>(type: "TEXT", nullable: true),
                    next_retry_at_utc = table.Column<string>(type: "TEXT", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    server_response_json = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_outbox_event", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_local_outbox_status_sequence",
                table: "local_outbox_event",
                columns: new[] { "status", "device_sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_local_outbox_sequence",
                table: "local_outbox_event",
                column: "device_sequence",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_sequence");

            migrationBuilder.DropTable(
                name: "local_outbox_event");
        }
    }
}
