using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Hands out the change feed's order, one transaction at a time.
/// </summary>
/// <remarks>
/// A single counter row, incremented inside the caller's transaction. It is
/// deliberately not a database identity column: identities can be handed out in
/// one order and committed in another, and a device reading "everything after my
/// cursor" would store the higher number and never see the lower one again.
/// Serialising here costs concurrency on master-data writes, which are rare; a
/// sale never touches it.
/// </remarks>
public static class ChangeFeedSequenceAllocator
{
    /// <summary>The counter row's name; there is one.</summary>
    public const string SequenceName = "change_feed";

    /// <summary>Takes one sequence value.</summary>
    /// <param name="context">The server database, inside the caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value.</returns>
    public static Task<long> NextAsync(PosDbContext context, CancellationToken cancellationToken)
        => NextAsync(context, 1, cancellationToken);

    /// <summary>
    /// Takes the next <paramref name="count"/> values, so a rolled-back change
    /// releases its numbers.
    /// </summary>
    /// <param name="context">The server database, inside the caller's transaction.</param>
    /// <param name="count">How many consecutive values are needed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first of them.</returns>
    public static async Task<long> NextAsync(
        PosDbContext context,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        DbConnection connection = context.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        // Assigned from a constant on each branch rather than through a variable,
        // so it is visibly not built from anything a caller supplied. The count
        // and the name travel as parameters.
        if (context.Database.IsSqlite())
        {
            command.CommandText = SqliteUpsert;
        }
        else
        {
            command.CommandText = PostgresUpsert;
        }

        DbParameter name = command.CreateParameter();
        name.ParameterName = "name";
        name.Value = SequenceName;
        command.Parameters.Add(name);

        DbParameter take = command.CreateParameter();
        take.ParameterName = "count";
        take.Value = count;
        command.Parameters.Add(take);

        object? allocated = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // The row holds one past what it has handed out, as the document
        // counters do, so the first sequence ever allocated is one.
        return Convert.ToInt64(allocated, CultureInfo.InvariantCulture) - count;
    }

    private const string PostgresUpsert =
        "INSERT INTO sync.feed_sequence (name, next_value) VALUES (@name, 1 + @count) " +
        "ON CONFLICT (name) DO UPDATE SET next_value = f.next_value + @count " +
        "WHERE f.name = @name RETURNING next_value";

    private const string SqliteUpsert =
        "INSERT INTO feed_sequence (name, next_value) VALUES (@name, 1 + @count) " +
        "ON CONFLICT (name) DO UPDATE SET next_value = next_value + @count " +
        "RETURNING next_value";
}
