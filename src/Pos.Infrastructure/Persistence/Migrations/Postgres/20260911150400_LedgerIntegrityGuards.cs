using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence.Sql;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <summary>
    /// Installs the database-level guards that make the inventory ledger
    /// tamper-evident: append-only triggers on the movement table, and a guard
    /// that rejects any balance change not backed by movements written in the
    /// same transaction.
    /// </summary>
    /// <remarks>
    /// Hand-written because triggers are not expressible in the model builder.
    /// The scripts are embedded resources so they can be reviewed like code.
    /// </remarks>
    [DbContext(typeof(PosDbContext))]
    [Migration("20260911150400_LedgerIntegrityGuards")]
    public partial class LedgerIntegrityGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(SqlResources.Read("Postgres/01_ledger_immutability.sql"));
            migrationBuilder.Sql(SqlResources.Read("Postgres/02_balance_guard.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_inventory_balance_no_delete ON inventory.inventory_balance;
                DROP TRIGGER IF EXISTS trg_inventory_balance_guard ON inventory.inventory_balance;
                DROP TRIGGER IF EXISTS trg_inventory_movement_no_truncate ON inventory.inventory_movement;
                DROP TRIGGER IF EXISTS trg_inventory_movement_immutable ON inventory.inventory_movement;
                DROP FUNCTION IF EXISTS inventory.deny_balance_delete();
                DROP FUNCTION IF EXISTS inventory.balance_matches_ledger();
                DROP FUNCTION IF EXISTS inventory.deny_mutation();
                """);
        }
    }
}
