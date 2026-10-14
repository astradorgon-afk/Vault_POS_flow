using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ActivityLogIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_transfer_custody_actor",
                schema: "transfers",
                table: "transfer_custody_event",
                columns: new[] { "actor_user_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_transfer_custody_occurred",
                schema: "transfers",
                table: "transfer_custody_event",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_occurred",
                schema: "audit",
                table: "audit_log",
                column: "occurred_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transfer_custody_actor",
                schema: "transfers",
                table: "transfer_custody_event");

            migrationBuilder.DropIndex(
                name: "ix_transfer_custody_occurred",
                schema: "transfers",
                table: "transfer_custody_event");

            migrationBuilder.DropIndex(
                name: "ix_audit_log_occurred",
                schema: "audit",
                table: "audit_log");
        }
    }
}
