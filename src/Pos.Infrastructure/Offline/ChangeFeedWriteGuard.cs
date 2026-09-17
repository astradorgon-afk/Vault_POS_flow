using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Pos.Infrastructure.Offline;

/// <summary>Marks a device table whose only legitimate writer is the change-feed applier.</summary>
internal interface IChangeFeedOwned;

/// <summary>
/// Thrown when code outside the change-feed applier attempts to write a
/// downloaded cache, a permission snapshot or the feed cursor.
/// </summary>
public sealed class ChangeFeedWriteViolationException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="ChangeFeedWriteViolationException"/> class.</summary>
    public ChangeFeedWriteViolationException()
        : base("Downloaded device data is written only by the change-feed applier.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ChangeFeedWriteViolationException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ChangeFeedWriteViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ChangeFeedWriteViolationException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ChangeFeedWriteViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Whether one device context is inside the change-feed applier's write window.
/// Internal, so application code in the client cannot open it.
/// </summary>
internal sealed class ChangeFeedWriteScope
{
    private int _open;

    /// <summary>Gets a value indicating whether applier-owned tables may be written.</summary>
    public bool IsOpen => Volatile.Read(ref _open) == 1;

    /// <summary>Opens the write window until the returned handle is disposed.</summary>
    public IDisposable Open()
    {
        if (Interlocked.CompareExchange(ref _open, 1, 0) != 0)
        {
            throw new InvalidOperationException("The change-feed write scope is already open.");
        }

        return new Closer(this);
    }

    private sealed class Closer(ChangeFeedWriteScope scope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Volatile.Write(ref scope._open, 0);
            }
        }
    }
}

/// <summary>
/// Rejects tracked changes to applier-owned entities outside the applier's
/// write scope, before any SQL is sent.
/// </summary>
/// <remarks>
/// The first of two guards. SQLite triggers on the same tables catch raw SQL and
/// bulk <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> statements that never reach
/// the change tracker.
/// </remarks>
internal sealed class ChangeFeedWriteGuardInterceptor : SaveChangesInterceptor
{
    /// <summary>Gets the shared stateless instance.</summary>
    public static ChangeFeedWriteGuardInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Guard(DbContext? context)
    {
        if (context is not PosDeviceDbContext device || device.ChangeFeedWrites.IsOpen)
        {
            return;
        }

        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.State is (EntityState.Added or EntityState.Modified or EntityState.Deleted)
                && entry.Entity is IChangeFeedOwned)
            {
                throw new ChangeFeedWriteViolationException(
                    FormattableString.Invariant(
                        $"{entry.Metadata.ClrType.Name} is downloaded device data and cannot be {entry.State.ToString().ToLowerInvariant()} outside the change-feed applier."));
            }
        }
    }
}

/// <summary>
/// Registers the SQL function the applier-owned table triggers consult, bound to
/// the write scope of the context that opened the connection.
/// </summary>
/// <remarks>
/// The function returns 1 only while that context's scope is open. A connection
/// opened outside a device context has no such function, so a trigger that
/// calls it fails the statement: the guard fails closed.
/// </remarks>
internal sealed class ChangeFeedWriterFunctionInterceptor : DbConnectionInterceptor
{
    /// <summary>The SQL function name used by the table triggers.</summary>
    public const string FunctionName = "vf_change_feed_writer";

    /// <summary>Gets the shared stateless instance.</summary>
    public static ChangeFeedWriterFunctionInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Register(connection, eventData);
        base.ConnectionOpened(connection, eventData);
    }

    /// <inheritdoc />
    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Register(connection, eventData);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Register(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (connection is not SqliteConnection sqlite || eventData.Context is not PosDeviceDbContext device)
        {
            return;
        }

        ChangeFeedWriteScope scope = device.ChangeFeedWrites;
        sqlite.CreateFunction(FunctionName, () => scope.IsOpen ? 1L : 0L, isDeterministic: false);
    }
}

/// <summary>
/// Registers the SQL function the device's ledger triggers consult, bound to the
/// ledger write window of the context that opened the connection.
/// </summary>
/// <remarks>
/// It is the same mechanism that protects the downloaded caches, for the same
/// reason: SQLite has no deferred triggers, so the device's balance guard cannot
/// check the arithmetic at commit the way PostgreSQL's does, and it asks who is
/// writing instead. A connection opened outside a device context has no such
/// function, so a trigger that calls it fails the statement — the guard fails
/// closed.
/// </remarks>
internal sealed class LedgerWriterFunctionInterceptor : DbConnectionInterceptor
{
    /// <summary>The SQL function name used by the ledger triggers.</summary>
    public const string FunctionName = "vf_ledger_writer";

    /// <summary>Gets the shared stateless instance.</summary>
    public static LedgerWriterFunctionInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Register(connection, eventData);
        base.ConnectionOpened(connection, eventData);
    }

    /// <inheritdoc />
    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Register(connection, eventData);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Register(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (connection is not SqliteConnection sqlite || eventData.Context is not PosDeviceDbContext device)
        {
            return;
        }

        ChangeFeedWriteScope scope = device.LedgerWrites;
        sqlite.CreateFunction(FunctionName, () => scope.IsOpen ? 1L : 0L, isDeterministic: false);
    }
}
