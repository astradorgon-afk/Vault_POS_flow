using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSaleReceiptPrintLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sale_receipt_print",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    printed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    printed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_reprint = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_receipt_print", x => x.id);
                    table.ForeignKey(
                        name: "FK_sale_receipt_print_sale_sale_id",
                        column: x => x.sale_id,
                        principalSchema: "sales",
                        principalTable: "sale",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sale_receipt_print_printed_at",
                schema: "sales",
                table: "sale_receipt_print",
                columns: new[] { "printed_at_utc", "is_reprint" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_receipt_print_sale",
                schema: "sales",
                table: "sale_receipt_print",
                columns: new[] { "sale_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sale_receipt_print",
                schema: "sales");
        }
    }
}
