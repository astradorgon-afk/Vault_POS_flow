using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SupplierDeliveryDocumentsAndDispatchDeliveryReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "delivery_receipt_number",
                schema: "transfers",
                table: "transfer_order",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "supplier_delivery_receipt_number",
                schema: "purchasing",
                table: "goods_receipt",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "supplier_official_receipt_number",
                schema: "purchasing",
                table: "goods_receipt",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "supplier_released_at_utc",
                schema: "purchasing",
                table: "goods_receipt",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delivery_receipt_number",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.DropColumn(
                name: "supplier_delivery_receipt_number",
                schema: "purchasing",
                table: "goods_receipt");

            migrationBuilder.DropColumn(
                name: "supplier_official_receipt_number",
                schema: "purchasing",
                table: "goods_receipt");

            migrationBuilder.DropColumn(
                name: "supplier_released_at_utc",
                schema: "purchasing",
                table: "goods_receipt");
        }
    }
}
