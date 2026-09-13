using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence.Sql;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <summary>
    /// Makes the audit log and the sign-in attempt history append-only at the
    /// database level, the same way the inventory ledger already is.
    /// </summary>
    /// <remarks>
    /// Hand-written because triggers are not expressible in the model builder.
    /// The script is an embedded resource so it can be reviewed like code.
    /// </remarks>
    [DbContext(typeof(PosDbContext))]
    [Migration("20260912034410_AuditIntegrityGuards")]
    public partial class AuditIntegrityGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(SqlResources.Read("Postgres/03_audit_immutability.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_login_attempt_immutable ON core.login_attempt;
                DROP TRIGGER IF EXISTS trg_audit_log_no_truncate ON audit.audit_log;
                DROP TRIGGER IF EXISTS trg_audit_log_immutable ON audit.audit_log;
                """);
        }
    }
}
