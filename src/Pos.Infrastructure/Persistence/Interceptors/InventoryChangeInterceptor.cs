using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Announces the locations whose stock moved, once the movement is durable.
/// </summary>
/// <remarks>
/// Every stock change posts an <see cref="InventoryMovement"/> through the
/// change tracker, so watching for new movements catches sales, returns,
/// transfers, counts, receipts and synced offline sales alike. Most postings
/// run inside a transaction; the announcement waits for its commit, so a client
/// that reloads on the signal never reads the figures from before the change,
/// and a rolled-back attempt announces nothing.
/// </remarks>
/// <param name="publisher">Where the announcement goes.</param>
/// <param name="logger">Records an announcement that could not be delivered.</param>
public sealed partial class InventoryChangeInterceptor(
    IInventoryChangePublisher publisher,
    ILogger<InventoryChangeInterceptor> logger) : ISaveChangesInterceptor, IDbTransactionInterceptor
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

        foreach (var entry in context.ChangeTracker.Entries<InventoryMovement>())
        {
            // External legs are the supplier or customer side of a movement,
            // not stock anybody watches.
            if (entry.State == EntityState.Added && entry.Entity.State != InventoryState.External)
            {
                _pending.GetOrCreateValue(context).Add(entry.Entity.LocationId);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not announce stock changes at {LocationCount} location(s).")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, int locationCount);
}

/// <summary>Announces nothing: used where no client listens, such as tests and tools.</summary>
public sealed class NoInventoryChangePublisher : IInventoryChangePublisher
{
    /// <inheritdoc />
    public Task PublishAsync(IReadOnlyCollection<LocationId> locations, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
