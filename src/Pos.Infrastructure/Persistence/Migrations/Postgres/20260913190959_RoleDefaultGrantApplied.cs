using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RoleDefaultGrantApplied : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "role_default_grant_applied",
                schema: "core",
                columns: table => new
                {
                    role_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    permission_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    applied_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_default_grant_applied", x => new { x.role_name, x.permission_code });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_default_grant_applied",
                schema: "core");
        }
    }
}
