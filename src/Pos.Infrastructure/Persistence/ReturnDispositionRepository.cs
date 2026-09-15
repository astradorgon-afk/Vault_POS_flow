using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence;

/// <summary>Persists return inspection history and tracks concurrent changes to each line's pending quantity.</summary>
public sealed class ReturnDispositionRepository(PosDbContext context, IUnitOfWork unitOfWork) : IReturnDispositionRepository
{
    /// <inheritdoc />
    public Task<SalesReturn?> GetReturnAsync(SalesReturnId id, CancellationToken cancellationToken)
        => context.SalesReturns.AsTracking().Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<SalesReturnDisposition?> GetEventAsync(EventId id, CancellationToken cancellationToken)
        => context.Set<SalesReturnDisposition>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task AddAsync(SalesReturnDisposition disposition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        context.Set<SalesReturnDisposition>().Add(disposition);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
