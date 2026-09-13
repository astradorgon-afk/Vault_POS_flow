using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence.Sql;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <summary>
    /// Replaces the immediate balance guard with a deferred constraint trigger.
    /// </summary>
    /// <remarks>
    /// The original fired per statement and so assumed Entity Framework would
    /// insert movements before the balances that summarise them. Nothing
    /// guarantees that order, and a real posting was rejected because of it.
    /// Running at commit removes the assumption and strengthens the check, which
    /// now sees the transaction's final state. See the script for the full note.
    /// </remarks>
    [DbContext(typeof(PosDbContext))]
    [Migration("20260912034411_BalanceGuardDeferred")]
    public partial class BalanceGuardDeferred : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(SqlResources.Read("Postgres/04_balance_guard_deferred.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Restores the immediate form. Kept only for completeness: the
            // project is forward-only, because a ledger cannot be un-inserted.
            migrationBuilder.Sql(SqlResources.Read("Postgres/02_balance_guard.sql"));
        }
    }
}
