using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Transfers;

namespace Pos.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Announces the locations whose transfer documents changed state, once the
/// change is durable.
/// </summary>
/// <remarks>
/// Every lifecycle step — submit, review, approve, reject, pick, dispatch,
/// receive, verify, cancel — rewrites a tracked <see cref="Transfer"/>, so
/// watching that entity catches each one without the command handlers having
/// to remember an announcement. Both ends are announced: the store waiting on
/// a restock request follows its destination side while the warehouse follows
/// the source. Commands run inside a transaction; the announcement waits for
/// its commit, so a client that reloads on the signal never reads the state
/// from before the change, and a rolled-back attempt announces nothing.
/// </remarks>
/// <param name="publisher">Where the announcement goes.</param>
/// <param name="logger">Records an announcement that could not be delivered.</param>
public sealed partial class TransferChangeInterceptor(
    ITransferChangePublisher publisher,
    ILogger<TransferChangeInterceptor> logger) : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly ConditionalWeakTable<DbContext, HashSet<LocationId>> _pending = [];

    /// <inheritdoc />
    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData?.Context);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData?.Context is { } context && context.Database.CurrentTransaction is null)
        {
            _ = FlushAsync(context);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData?.Context is { } context && context.Database.CurrentTransaction is null)
        {
            await FlushAsync(context).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData?.Context is { } context && context.Database.CurrentTransaction is null)
        {
            _pending.Remove(context);
        }
    }

    /// <inheritdoc />
    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesFailed(eventData);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData?.Context is { } context)
        {
            _ = FlushAsync(context);
        }
    }

    /// <inheritdoc />
    public async Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData?.Context is { } context)
        {
            await FlushAsync(context).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData?.Context is { } context)
        {
            _pending.Remove(context);
        }
    }

    /// <inheritdoc />
    public Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionRolledBack(transaction, eventData);
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<Transfer>())
        {
            // A fresh document changes the lists it appears in; a status step
            // changes where it sits in its workflow. Nothing else about a
            // transfer is worth waking a page for.
            bool isNew = entry.State == EntityState.Added;
            bool statusChanged = entry.State == EntityState.Modified
                && entry.Property(nameof(Transfer.Status)).IsModified;

            if (isNew || statusChanged)
            {
                HashSet<LocationId> locations = _pending.GetOrCreateValue(context);
                locations.Add(entry.Entity.SourceLocationId);
                locations.Add(entry.Entity.DestinationLocationId);
            }
        }
    }

    private async Task FlushAsync(DbContext context)
    {
        if (!_pending.TryGetValue(context, out HashSet<LocationId>? locations))
        {
            return;
        }

        _pending.Remove(context);
        if (locations.Count == 0)
        {
            return;
        }

        try
        {
            // The change is already committed; a caller cancelling now must
            // not stop the announcement of it.
            await publisher.PublishAsync([.. locations], CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A lost live hint must never surface as a failed command.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPublishFailed(logger, ex, locations.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not announce transfer changes at {LocationCount} location(s).")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, int locationCount);
}

/// <summary>Announces nothing: used where no client listens, such as tests and tools.</summary>
public sealed class NoTransferChangePublisher : ITransferChangePublisher
{
    /// <inheritdoc />
    public Task PublishAsync(IReadOnlyCollection<LocationId> locations, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
