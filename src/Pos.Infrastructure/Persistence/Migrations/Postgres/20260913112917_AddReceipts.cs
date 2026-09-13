using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "receipt",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    counterparty = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    reference_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_receipt_location_time",
                schema: "core",
                table: "receipt",
                columns: new[] { "location_id", "issued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_receipt_number",
                schema: "core",
                table: "receipt",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "receipt",
                schema: "core");
        }
    }
}
