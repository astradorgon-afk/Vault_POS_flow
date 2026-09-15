using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SalesReturnDispositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.AddColumn<decimal>(
                name: "dispositioned_quantity",
                schema: "sales",
                table: "sales_return_item",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "sales_return_disposition",
                schema: "sales",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_return_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    reason_code = table.Column<short>(type: "smallint", nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_return_disposition", x => x.event_id);
                    table.ForeignKey(
                        name: "FK_sales_return_disposition_sales_return_item_sales_return_ite~",
                        column: x => x.sales_return_item_id,
                        principalSchema: "sales",
                        principalTable: "sales_return_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_return_disposition_sales_return_sales_return_id",
                        column: x => x.sales_return_id,
                        principalSchema: "sales",
                        principalTable: "sales_return",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_disposition_sales_return_id",
                schema: "sales",
                table: "sales_return_disposition",
                column: "sales_return_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_disposition_sales_return_item_id",
                schema: "sales",
                table: "sales_return_disposition",
                column: "sales_return_item_id");

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_return_disposition_immutable
                    BEFORE UPDATE OR DELETE ON sales.sales_return_disposition
                    FOR EACH ROW EXECUTE FUNCTION inventory.deny_mutation();
                CREATE TRIGGER trg_return_disposition_no_truncate
                    BEFORE TRUNCATE ON sales.sales_return_disposition
                    FOR EACH STATEMENT EXECUTE FUNCTION inventory.deny_mutation();
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pos_app') THEN
                        REVOKE UPDATE, DELETE, TRUNCATE ON sales.sales_return_disposition FROM pos_app;
                        GRANT SELECT, INSERT ON sales.sales_return_disposition TO pos_app;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropTable(
                name: "sales_return_disposition",
                schema: "sales");

            migrationBuilder.DropColumn(
                name: "dispositioned_quantity",
                schema: "sales",
                table: "sales_return_item");
        }
    }
}
