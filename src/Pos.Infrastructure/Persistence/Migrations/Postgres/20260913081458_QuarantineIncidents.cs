using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class QuarantineIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "quarantine");

            migrationBuilder.CreateTable(
                name: "quarantine_incident",
                schema: "quarantine",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    investigated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quarantine_incident", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quarantine_event",
                schema: "quarantine",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quarantine_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_quarantine_event_quarantine_incident_incident_id",
                        column: x => x.incident_id,
                        principalSchema: "quarantine",
                        principalTable: "quarantine_incident",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quarantine_incident_line",
                schema: "quarantine",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    barcode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    claimed_product_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dispositioned_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    disposition = table.Column<short>(type: "smallint", nullable: false),
                    dispositioned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dispositioned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disposition_note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quarantine_incident_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_quarantine_incident_line_quarantine_incident_incident_id",
                        column: x => x.incident_id,
                        principalSchema: "quarantine",
                        principalTable: "quarantine_incident",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quarantine_photo",
                schema: "quarantine",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quarantine_photo", x => x.id);
                    table.ForeignKey(
                        name: "FK_quarantine_photo_quarantine_incident_incident_id",
                        column: x => x.incident_id,
                        principalSchema: "quarantine",
                        principalTable: "quarantine_incident",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_quarantine_incident_event_sequence",
                schema: "quarantine",
                table: "quarantine_event",
                columns: new[] { "incident_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quarantine_incident_location_time",
                schema: "quarantine",
                table: "quarantine_incident",
                columns: new[] { "location_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_quarantine_incident_status_time",
                schema: "quarantine",
                table: "quarantine_incident",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_quarantine_incident_number",
                schema: "quarantine",
                table: "quarantine_incident",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_quarantine_incident_line_position",
                schema: "quarantine",
                table: "quarantine_incident_line",
                columns: new[] { "incident_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quarantine_photo_incident_time",
                schema: "quarantine",
                table: "quarantine_photo",
                columns: new[] { "incident_id", "uploaded_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quarantine_event",
                schema: "quarantine");

            migrationBuilder.DropTable(
                name: "quarantine_incident_line",
                schema: "quarantine");

            migrationBuilder.DropTable(
                name: "quarantine_photo",
                schema: "quarantine");

            migrationBuilder.DropTable(
                name: "quarantine_incident",
                schema: "quarantine");
        }
    }
}
