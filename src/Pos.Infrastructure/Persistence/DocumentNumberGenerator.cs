using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Allocates central document numbers from the <c>core.document_counter</c>
/// table. The allocation is a single atomic upsert that runs inside the
/// caller's transaction, so two concurrent submissions can never receive the
/// same number even if one later rolls back.
/// </summary>
/// <remarks>
/// <para>
/// The insert-returning form is deliberately provider-neutral: PostgreSQL and
/// SQLite both support <c>ON CONFLICT ... DO UPDATE</c> with <c>RETURNING</c>.
/// A fresh row starts at two so the allocated sequence (returned value minus
/// one) begins at one. SQLite's <c>RETURNING</c> cannot evaluate expressions,
/// so the subtraction happens in C# rather than the SQL.
/// </para>
/// <para>
/// The table name is provider-aware because SQLite ignores EF schemas: the same
/// SQL runs against <c>document_counter</c> there and
/// <c>core.document_counter</c> on PostgreSQL.
/// </para>
/// </remarks>
/// <param name="context">The database context, whose open transaction is reused.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class DocumentNumberGenerator(PosDbContext context, ISystemClock clock) : IDocumentNumberGenerator
{
    /// <inheritdoc />
    public async Task<DocumentNumber> NextAsync(DocumentType type, CancellationToken cancellationToken)
    {
        if (DocumentNumber.IsDeviceScoped(type))
        {
            throw new NotSupportedException(
                FormattableString.Invariant(
                    $"{type}: device-scoped numbers are issued by the device counter, not the server."));
        }

        long nextValue = await AllocateAsync(type, scopeKey: string.Empty, cancellationToken).ConfigureAwait(false);
        return DocumentNumber.Create(type, clock.UtcNow.Year, nextValue - 1);
    }

    /// <inheritdoc />
    public async Task<DocumentNumber> NextScopedAsync(DocumentType type, string deviceShortCode, CancellationToken cancellationToken)
    {
        if (!DocumentNumber.IsDeviceScoped(type))
        {
            throw new NotSupportedException(
                FormattableString.Invariant(
                    $"{type}: only device-scoped numbers may be allocated per device, not {type}."));
        }

        if (string.IsNullOrWhiteSpace(deviceShortCode))
        {
            throw new ArgumentException("A device short code is required.", nameof(deviceShortCode));
        }

        long nextValue = await AllocateAsync(type, deviceShortCode.Trim().ToUpperInvariant(), cancellationToken)
            .ConfigureAwait(false);
        return DocumentNumber.CreateForDevice(type, clock.UtcNow.Year, deviceShortCode.Trim().ToUpperInvariant(), nextValue - 1);
    }

    private async Task<long> AllocateAsync(DocumentType type, string scopeKey, CancellationToken cancellationToken)
    {
        int year = clock.UtcNow.Year;
        string periodKey = year.ToString("D4", CultureInfo.InvariantCulture);

        DbConnection connection = context.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using DbCommand command = connection.CreateCommand();

        // The two statements differ only in the schema-qualified table name:
        // SQLite ignores EF schemas, PostgreSQL does not. Both are constants,
        // never derived from input.
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        if (context.IsSqlite)
        {
            command.CommandText = SqliteUpsert;
        }
        else
        {
            command.CommandText = PostgresUpsert;
        }

        command.Parameters.Add(Parameter(command, "type", (short)type));
        command.Parameters.Add(Parameter(command, "period", periodKey));
        command.Parameters.Add(Parameter(command, "scope", scopeKey));

        object? allocated = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(allocated, CultureInfo.InvariantCulture);
    }

    private const string SqliteUpsert =
        "INSERT INTO document_counter (document_type, period_key, scope_key, next_value) " +
        "VALUES (@type, @period, @scope, 2) " +
        "ON CONFLICT (document_type, period_key, scope_key) " +
        "DO UPDATE SET next_value = next_value + 1 " +
        "RETURNING next_value";

    // PostgreSQL rejects the unqualified column in DO UPDATE as ambiguous between
    // the existing row and EXCLUDED (42702), so the target is aliased; SQLite
    // resolves it to the existing row and accepts the short form.
    private const string PostgresUpsert =
        "INSERT INTO core.document_counter AS c (document_type, period_key, scope_key, next_value) " +
        "VALUES (@type, @period, @scope, 2) " +
        "ON CONFLICT (document_type, period_key, scope_key) " +
        "DO UPDATE SET next_value = c.next_value + 1 " +
        "RETURNING c.next_value";

    private static DbParameter Parameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }
}