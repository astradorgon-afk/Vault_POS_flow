using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ProductBarcodeRetirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retired_at_utc",
                schema: "catalog",
                table: "product_barcode",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "retired_by",
                schema: "catalog",
                table: "product_barcode",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "retired_at_utc",
                schema: "catalog",
                table: "product_barcode");

            migrationBuilder.DropColumn(
                name: "retired_by",
                schema: "catalog",
                table: "product_barcode");
        }
    }
}
