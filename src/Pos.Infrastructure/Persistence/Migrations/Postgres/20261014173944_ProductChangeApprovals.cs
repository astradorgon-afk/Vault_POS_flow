using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ProductChangeApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_change_request",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    summary = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    payload = table.Column<string>(type: "text", maxLength: 0, nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_change_request", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_product_change_request_product",
                schema: "catalog",
                table: "product_change_request",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_change_request_status",
                schema: "catalog",
                table: "product_change_request",
                columns: new[] { "status", "requested_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_change_request",
                schema: "catalog");
        }
    }
}
