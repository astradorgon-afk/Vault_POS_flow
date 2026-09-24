using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// The transaction boundary for a business operation, backed by EF Core.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class UnitOfWork(PosDbContext context) : IUnitOfWork
{
    /// <inheritdoc />
    public bool HasActiveTransaction => context.Database.CurrentTransaction is not null;

    /// <inheritdoc />
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException ex)
        {
            // The provider's exception is storage-specific; the application
            // layer must map contention without knowing which provider. See the
            // remarks on ConcurrencyConflictException.
            throw new ConcurrencyConflictException(ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Besides a stale balance version, two writers that both found a balance
    /// row missing race to insert it and the loser trips the primary key; the
    /// ledger treats both as the same competition, and so does this.
    /// </remarks>
    public bool IsConcurrencyConflict(Exception exception)
        => exception is ConcurrencyConflictException or Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException
           || (exception is Microsoft.EntityFrameworkCore.DbUpdateException update && Inventory.InventoryLedger.IsBalanceCompetition(update));

    /// <inheritdoc />
    public void DiscardChanges() => context.ChangeTracker.Clear();

    /// <inheritdoc />
    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        IDbContextTransaction transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        return new EfTransaction(transaction);
    }

    private sealed class EfTransaction(IDbContextTransaction transaction) : IUnitOfWorkTransaction
    {
        private bool _completed;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            // A transaction that leaves scope without an explicit outcome is a
            // failure path: roll back rather than let the provider decide.
            if (!_completed)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}
