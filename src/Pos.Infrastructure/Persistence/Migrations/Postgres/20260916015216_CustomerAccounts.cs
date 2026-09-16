using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class CustomerAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "customer",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    tin = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deactivated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deactivation_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer", x => x.id);
                });

            // CustomerId was an unconstrained optional field before customer accounts
            // existed. Preserve any historical references as inactive records before
            // adding the foreign keys so an upgraded database remains migratable.
            migrationBuilder.Sql(
                """
                WITH customer_references AS (
                    SELECT customer_id,
                           completed_at_utc AS occurred_at_utc,
                           completed_by_user_id AS actor_id
                    FROM sales.sale
                    WHERE customer_id IS NOT NULL

                    UNION ALL

                    SELECT customer_id,
                           returned_at_utc AS occurred_at_utc,
                           returned_by_user_id AS actor_id
                    FROM sales.sales_return
                    WHERE customer_id IS NOT NULL
                ),
                first_reference AS (
                    SELECT DISTINCT ON (customer_id)
                           customer_id,
                           occurred_at_utc,
                           actor_id
                    FROM customer_references
                    ORDER BY customer_id, occurred_at_utc
                )
                INSERT INTO sales.customer (
                    id,
                    display_name,
                    is_active,
                    created_at_utc,
                    created_by_user_id,
                    updated_at_utc,
                    updated_by_user_id,
                    deactivated_at_utc,
                    deactivation_reason)
                SELECT customer_id,
                       'Legacy customer ' || LEFT(customer_id::text, 8),
                       FALSE,
                       occurred_at_utc,
                       actor_id,
                       occurred_at_utc,
                       actor_id,
                       occurred_at_utc,
                       'Imported reference pending customer verification'
                FROM first_reference
                ON CONFLICT (id) DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_customer_id",
                schema: "sales",
                table: "sales_return",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_sale_customer_id",
                schema: "sales",
                table: "sale",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_display_name",
                schema: "sales",
                table: "customer",
                column: "display_name");

            migrationBuilder.CreateIndex(
                name: "ix_customer_is_active",
                schema: "sales",
                table: "customer",
                column: "is_active");

            migrationBuilder.AddForeignKey(
                name: "FK_sale_customer_customer_id",
                schema: "sales",
                table: "sale",
                column: "customer_id",
                principalSchema: "sales",
                principalTable: "customer",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sales_return_customer_customer_id",
                schema: "sales",
                table: "sales_return",
                column: "customer_id",
                principalSchema: "sales",
                principalTable: "customer",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_sale_customer_customer_id",
                schema: "sales",
                table: "sale");

            migrationBuilder.DropForeignKey(
                name: "FK_sales_return_customer_customer_id",
                schema: "sales",
                table: "sales_return");

            migrationBuilder.DropTable(
                name: "customer",
                schema: "sales");

            migrationBuilder.DropIndex(
                name: "IX_sales_return_customer_id",
                schema: "sales",
                table: "sales_return");

            migrationBuilder.DropIndex(
                name: "IX_sale_customer_id",
                schema: "sales",
                table: "sale");
        }
    }
}
