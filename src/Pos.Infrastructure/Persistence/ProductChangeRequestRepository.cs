using Microsoft.EntityFrameworkCore;
using Pos.Application.Catalog;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Infrastructure.Identity;

namespace Pos.Infrastructure.Persistence;

/// <summary>Reads and writes catalogue changes held for approval.</summary>
/// <param name="context">The database context.</param>
public sealed class ProductChangeRequestRepository(PosDbContext context) : IProductChangeRequestRepository
{
    /// <inheritdoc />
    public async Task AddAsync(ProductChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await context.ProductChangeRequests.AddAsync(request, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ProductChangeRequest?> GetForUpdateAsync(
        ProductChangeRequestId requestId,
        CancellationToken cancellationToken)
        => context.ProductChangeRequests
            .AsTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductChangeRequestView>> ListAsync(
        bool pendingOnly,
        ProductId? productId,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<ProductChangeRequest> query = context.ProductChangeRequests.AsNoTracking();

        if (pendingOnly)
        {
            query = query.Where(r => r.Status == ProductChangeRequestStatus.Pending);
        }

        if (productId is { } id)
        {
            query = query.Where(r => r.ProductId == id);
        }

        // Pending first so the queue leads with what still needs a decision,
        // then newest, so a long history does not bury today's requests.
        var rows = await query
            .OrderBy(r => r.Status == ProductChangeRequestStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.RequestedAtUtc)
            .Take(limit)
            .Join(
                context.Products.AsNoTracking(),
                r => r.ProductId,
                p => p.Id,
                (r, p) => new { Request = r, ProductName = p.Name, ProductSku = p.Sku })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Guid[] actors =
        [
            .. rows.Select(row => row.Request.RequestedBy.Value)
                .Concat(rows.Where(row => row.Request.DecidedBy != null)
                    .Select(row => row.Request.DecidedBy!.Value.Value))
                .Distinct(),
        ];

        Dictionary<Guid, string> names = await context.Set<AppUser>()
            .AsNoTracking()
            .Where(u => actors.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new ProductChangeRequestView(
                row.Request.Id.Value,
                row.Request.ProductId.Value,
                row.ProductName,
                row.ProductSku.Value,
                row.Request.ChangeType,
                row.Request.Summary,
                row.Request.Reason,
                row.Request.Status.ToString(),
                row.Request.RequestedBy.Value,
                names.GetValueOrDefault(row.Request.RequestedBy.Value),
                row.Request.RequestedAtUtc,
                row.Request.DecidedBy is { } decider ? names.GetValueOrDefault(decider.Value) : null,
                row.Request.DecidedAtUtc,
                row.Request.DecisionNote,
                row.Request.FailureReason)),
        ];
    }
}
