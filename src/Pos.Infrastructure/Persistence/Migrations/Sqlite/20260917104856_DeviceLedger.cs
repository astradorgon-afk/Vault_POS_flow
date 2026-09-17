using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_inventory_balance",
                columns: table => new
                {
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    batch_key = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<short>(type: "INTEGER", nullable: false),
                    quantity = table.Column<string>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    average_unit_cost = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    total_value = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    last_movement_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_movement_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_inventory_balance", x => new { x.location_id, x.product_id, x.batch_key, x.state });
                });

            migrationBuilder.CreateTable(
                name: "local_inventory_movement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    movement_group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    leg_number = table.Column<short>(type: "INTEGER", nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    batch_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    location_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<short>(type: "INTEGER", nullable: false),
                    quantity_delta = table.Column<string>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    total_value_delta = table.Column<string>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    movement_type = table.Column<short>(type: "INTEGER", nullable: false),
                    source_location_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    destination_location_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    source_state = table.Column<short>(type: "INTEGER", nullable: true),
                    destination_state = table.Column<short>(type: "INTEGER", nullable: true),
                    reference_document_type = table.Column<short>(type: "INTEGER", nullable: false),
                    reference_document_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    reference_number = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    occurred_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    recorded_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    business_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    reason_code = table.Column<short>(type: "INTEGER", nullable: true),
                    notes = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    reverses_movement_group_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    sync_status = table.Column<short>(type: "INTEGER", nullable: false),
                    server_processing_status = table.Column<short>(type: "INTEGER", nullable: false),
                    correlation_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_inventory_movement", x => x.id);
                    table.CheckConstraint("ck_inventory_movement_cost_nonnegative", "unit_cost >= 0");
                    table.CheckConstraint("ck_inventory_movement_leg_positive", "leg_number >= 1");
                    table.CheckConstraint("ck_inventory_movement_quantity_nonzero", "quantity_delta <> 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_balance_location_state",
                table: "local_inventory_balance",
                columns: new[] { "location_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_balance_product_location",
                table: "local_inventory_balance",
                columns: new[] { "product_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_bucket",
                table: "local_inventory_movement",
                columns: new[] { "location_id", "product_id", "batch_id", "state", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_event",
                table: "local_inventory_movement",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_group",
                table: "local_inventory_movement",
                column: "movement_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_product_time",
                table: "local_inventory_movement",
                columns: new[] { "product_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_reference",
                table: "local_inventory_movement",
                columns: new[] { "reference_document_type", "reference_document_id" });

            migrationBuilder.CreateIndex(
                name: "ux_inventory_movement_group_leg",
                table: "local_inventory_movement",
                columns: new[] { "movement_group_id", "leg_number" },
                unique: true);

            // Layer 3 of the four that stop `product.StockQuantity = 100`
            // (README, OFFLINE_SYNC.md §2). The device has no database roles, so
            // three of the four layers carry here: the domain types have no
            // setters, the EF interceptor refuses tracked mutations, and these
            // triggers refuse anything that reaches the file another way. The
            // fourth, a least-privilege role, has no SQLite equivalent — what
            // stands in its place is the encryption key, which is why the key
            // lives in the platform secure store and never in the file.
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_local_inventory_movement_immutable_update
                BEFORE UPDATE ON local_inventory_movement
                BEGIN
                    SELECT RAISE(ABORT, 'local_inventory_movement is append-only; post a reversing entry instead');
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_local_inventory_movement_immutable_delete
                BEFORE DELETE ON local_inventory_movement
                BEGIN
                    SELECT RAISE(ABORT, 'local_inventory_movement is append-only; post a reversing entry instead');
                END;
                """);

            // Only the ledger may write a balance. PostgreSQL checks the
            // arithmetic instead — its guard is a DEFERRED constraint trigger
            // that runs at commit, when every movement is in place — but SQLite
            // has no deferred triggers, so a BEFORE trigger here cannot see
            // movements EF has not inserted yet. It asks who is writing instead,
            // through the same per-connection function that protects the
            // downloaded caches. A connection opened outside a device context
            // has no such function, so the statement fails closed.
            foreach (string statement in new[] { "INSERT", "UPDATE" })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER trg_local_inventory_balance_ledger_only_{statement.ToLowerInvariant()}
                    BEFORE {statement} ON local_inventory_balance
                    WHEN vf_ledger_writer() IS NOT 1
                    BEGIN
                        SELECT RAISE(ABORT, 'Stock may only change through the inventory ledger');
                    END;
                    """);
            }

            // A deleted balance row would silently discard stock.
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_local_inventory_balance_ledger_only_delete
                BEFORE DELETE ON local_inventory_balance
                BEGIN
                    SELECT RAISE(ABORT, 'Inventory balances are not deletable; post a movement that brings the bucket to zero');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_local_inventory_balance_ledger_only_delete;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_local_inventory_balance_ledger_only_update;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_local_inventory_balance_ledger_only_insert;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_local_inventory_movement_immutable_delete;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_local_inventory_movement_immutable_update;");

            migrationBuilder.DropTable(
                name: "local_inventory_balance");

            migrationBuilder.DropTable(
                name: "local_inventory_movement");
        }
    }
}
