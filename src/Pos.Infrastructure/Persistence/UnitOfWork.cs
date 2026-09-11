using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;

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
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        => context.SaveChangesAsync(cancellationToken);

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
