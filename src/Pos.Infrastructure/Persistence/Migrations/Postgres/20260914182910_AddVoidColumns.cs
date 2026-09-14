using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddVoidColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "void_reason",
                schema: "sales",
                table: "sale",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "voided_at_utc",
                schema: "sales",
                table: "sale",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "voided_by_user_id",
                schema: "sales",
                table: "sale",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "void_reason",
                schema: "sales",
                table: "sale");

            migrationBuilder.DropColumn(
                name: "voided_at_utc",
                schema: "sales",
                table: "sale");

            migrationBuilder.DropColumn(
                name: "voided_by_user_id",
                schema: "sales",
                table: "sale");
        }
    }
}
