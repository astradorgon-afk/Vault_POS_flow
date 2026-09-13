using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence.Sql;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <summary>
    /// Records the stock draws the ledger refuses, append-only (triggers in
    /// <c>06_negative_stock_attempt_immutability.sql</c>).
    /// </summary>
    public partial class NegativeStockAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "negative_stock_attempt",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_type = table.Column<short>(type: "smallint", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_key = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    requested_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    available_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    policy = table.Column<short>(type: "smallint", nullable: false),
                    reference_document_type = table.Column<short>(type: "smallint", nullable: false),
                    reference_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_negative_stock_attempt", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_negative_stock_attempt_location_time",
                schema: "inventory",
                table: "negative_stock_attempt",
                columns: new[] { "location_id", "attempted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_negative_stock_attempt_product_time",
                schema: "inventory",
                table: "negative_stock_attempt",
                columns: new[] { "product_id", "attempted_at_utc" });

            migrationBuilder.Sql(SqlResources.Read("Postgres/06_negative_stock_attempt_immutability.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Dropping the table drops its triggers with it.
            migrationBuilder.DropTable(
                name: "negative_stock_attempt",
                schema: "inventory");
        }
    }
}
