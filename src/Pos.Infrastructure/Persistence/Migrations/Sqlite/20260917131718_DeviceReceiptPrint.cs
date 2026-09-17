using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceReceiptPrint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_sale_receipt_print",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sale_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    printed_by_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    printed_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    is_reprint = table.Column<bool>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_sale_receipt_print", x => x.id);
                    table.ForeignKey(
                        name: "FK_local_sale_receipt_print_local_sale_sale_id",
                        column: x => x.sale_id,
                        principalTable: "local_sale",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sale_receipt_print_printed_at",
                table: "local_sale_receipt_print",
                columns: new[] { "printed_at_utc", "is_reprint" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_receipt_print_sale",
                table: "local_sale_receipt_print",
                columns: new[] { "sale_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_sale_receipt_print");
        }
    }
}
