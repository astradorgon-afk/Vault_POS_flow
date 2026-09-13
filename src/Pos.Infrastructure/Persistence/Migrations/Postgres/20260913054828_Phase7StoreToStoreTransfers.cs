using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase7StoreToStoreTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_product_location_thresholds",
                schema: "catalog",
                table: "product_location_setting");

            migrationBuilder.AddColumn<Guid>(
                name: "emergency_ledger_group_id",
                schema: "transfers",
                table: "transfer_order",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "kind",
                schema: "transfers",
                table: "transfer_order",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddColumn<short>(
                name: "mode",
                schema: "transfers",
                table: "transfer_order",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddColumn<Guid>(
                name: "pre_approval_token_id",
                schema: "transfers",
                table: "transfer_order",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_note",
                schema: "transfers",
                table: "transfer_order",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "review_outcome",
                schema: "transfers",
                table: "transfer_order",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reviewed_at_utc",
                schema: "transfers",
                table: "transfer_order",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reviewed_by_user_id",
                schema: "transfers",
                table: "transfer_order",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pre_approval_token",
                schema: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_value = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    consumed_by_transfer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pre_approval_token", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pre_approval_token_product",
                schema: "transfers",
                columns: table => new
                {
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pre_approval_token_product", x => new { x.token_id, x.ordinal });
                    table.ForeignKey(
                        name: "FK_pre_approval_token_product_pre_approval_token_token_id",
                        column: x => x.token_id,
                        principalSchema: "transfers",
                        principalTable: "pre_approval_token",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_status_time",
                schema: "transfers",
                table: "transfer_order",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_location_thresholds",
                schema: "catalog",
                table: "product_location_setting",
                sql: "CAST(minimum_stock AS NUMERIC) <= CAST(reorder_point AS NUMERIC) AND CAST(reorder_point AS NUMERIC) <= CAST(target_stock AS NUMERIC) AND CAST(target_stock AS NUMERIC) <= CAST(maximum_stock AS NUMERIC)");

            migrationBuilder.CreateIndex(
                name: "ix_pre_approval_token_source_time",
                schema: "transfers",
                table: "pre_approval_token",
                columns: new[] { "source_location_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_pre_approval_token_number",
                schema: "transfers",
                table: "pre_approval_token",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pre_approval_token_product",
                schema: "transfers");

            migrationBuilder.DropTable(
                name: "pre_approval_token",
                schema: "transfers");

            migrationBuilder.DropIndex(
                name: "ix_transfer_order_status_time",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_location_thresholds",
                schema: "catalog",
                table: "product_location_setting");

            migrationBuilder.DropColumn(
                name: "emergency_ledger_group_id",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.DropColumn(
                name: "mode",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.DropColumn(
                name: "pre_approval_token_id",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.DropColumn(
                name: "review_note",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.DropColumn(
                name: "review_outcome",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.DropColumn(
                name: "reviewed_at_utc",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.DropColumn(
                name: "reviewed_by_user_id",
                schema: "transfers",
                table: "transfer_order");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_location_thresholds",
                schema: "catalog",
                table: "product_location_setting",
                sql: "minimum_stock <= reorder_point AND reorder_point <= target_stock AND target_stock <= maximum_stock");
        }
    }
}
