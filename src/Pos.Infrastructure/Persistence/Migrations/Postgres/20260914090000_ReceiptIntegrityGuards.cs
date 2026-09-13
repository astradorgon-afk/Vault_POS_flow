using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence.Sql;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <summary>
    /// Makes payment receipts append-only at the database level, the same way the
    /// inventory ledger and the audit log already are.
    /// </summary>
    /// <remarks>
    /// Hand-written because triggers are not expressible in the model builder.
    /// The script is an embedded resource so it can be reviewed like code.
    /// </remarks>
    [DbContext(typeof(PosDbContext))]
    [Migration("20260914090000_ReceiptIntegrityGuards")]
    public partial class ReceiptIntegrityGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(SqlResources.Read("Postgres/05_receipt_immutability.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_receipt_no_truncate ON core.receipt;
                DROP TRIGGER IF EXISTS trg_receipt_immutable ON core.receipt;
                """);
        }
    }
}
