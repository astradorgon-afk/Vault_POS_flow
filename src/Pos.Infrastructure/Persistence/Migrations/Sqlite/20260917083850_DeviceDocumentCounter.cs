using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceDocumentCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_counter",
                columns: table => new
                {
                    document_type = table.Column<short>(type: "INTEGER", nullable: false),
                    period_key = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    next_value = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_counter", x => new { x.document_type, x.period_key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_counter");
        }
    }
}
