using Microsoft.EntityFrameworkCore;
using Pos.Application.Reports;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;
using Pos.Domain.Reports;

namespace Pos.Infrastructure.Persistence;

/// <inheritdoc cref="IPurchasingReportRepository" />
/// <remarks>
/// <para>
/// Both reports key off the destination location, which is where the goods were
/// going and therefore whose business the order is. A supplier is not scoped:
/// suppliers are a business-wide list, and a store manager who orders from one
/// may see how that supplier has treated their store.
/// </para>
/// <para>
/// Punctuality is scored only where there is something to score. An order with no
/// expected date was never promised, and counting it as on time would reward a
/// supplier for refusing to commit to one.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class PurchasingReportRepository(PosDbContext context) : IPurchasingReportRepository
{
    /// <inheritdoc />
    public async Task<PurchaseOrderReport> GetPurchaseOrdersAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        SupplierId? supplierId,
        int limit,
        CancellationToken cancellationToken)
    {
        List<Order> orders = await OrdersAsync(fromUtc, toUtc, locations, supplierId, cancellationToken)
            .ConfigureAwait(false);

        List<PurchaseOrderReportRow> all =
        [
            .. orders
                .Select(o => new PurchaseOrderReportRow(
                    o.Po.Id.Value,
                    o.Po.Number,
                    o.Po.Status,
                    o.Po.SupplierId.Value,
                    o.SupplierName,
                    o.Po.DestinationLocationId.Value,
                    o.DestinationCode,
                    o.Po.CreatedAtUtc,
                    o.Po.OrderedAtUtc,
                    o.Po.ExpectedAtUtc,
                    o.FirstReceivedAtUtc,
                    o.Po.Lines.Count,
                    o.Po.Lines.Sum(l => l.OrderedQuantity),
                    o.ReceivedQuantity,
                    o.AcceptedQuantity,
                    o.Po.CurrencyCode,
                    o.Po.GrandTotal))
                .OrderByDescending(r => r.CreatedAtUtc)
                .ThenBy(r => r.Number, StringComparer.Ordinal),
        ];

        List<PurchaseOrderReportRow> rows = [.. all.Take(limit)];

        return new PurchaseOrderReport(fromUtc, toUtc, rows, Truncated: all.Count > rows.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SupplierPerformanceRow>> GetSupplierPerformanceAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken)
    {
        List<Order> orders = await OrdersAsync(fromUtc, toUtc, locations, null, cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. orders
                .GroupBy(o => o.Po.SupplierId)
                .Select(Score)
                .OrderByDescending(r => r.OrdersPlaced)
                .ThenBy(r => r.SupplierCode, StringComparer.Ordinal),
        ];
    }

    private static SupplierPerformanceRow Score(IGrouping<SupplierId, Order> g)
    {
        Order first = g.First();

        // Only orders that were both promised a date and actually received can be
        // scored for punctuality. Everything else is unknown, not on time.
        List<double> lateness =
        [
            .. g.Where(o => o.Po.ExpectedAtUtc is not null && o.FirstReceivedAtUtc is not null)
                .Select(o => (o.FirstReceivedAtUtc!.Value - o.Po.ExpectedAtUtc!.Value).TotalDays),
        ];

        decimal ordered = g.Sum(o => o.Po.Lines.Sum(l => l.OrderedQuantity));
        decimal accepted = g.Sum(o => o.AcceptedQuantity);

        return new SupplierPerformanceRow(
            g.Key.Value,
            first.SupplierCode,
            first.SupplierName,
            g.Count(),
            g.Count(o => o.FirstReceivedAtUtc is not null),
            lateness.Count,
            lateness.Count == 0 ? null : Rate(lateness.Count(d => d <= 0), lateness.Count),
            lateness.Count == 0 ? null : decimal.Round((decimal)lateness.Average(), 1, MidpointRounding.AwayFromZero),
            ordered,
            accepted,

            // Nothing ordered is no fill rate to state, rather than a perfect or a
            // hopeless one.
            ordered == 0m ? null : Rate(accepted, ordered),
            g.Sum(o => o.RejectedQuantity),
            g.Sum(o => o.ReceiptsWithMissingDocuments),
            g.Sum(o => o.ReceiptsWithCostVariance));
    }

    private static decimal Rate(decimal part, decimal whole)
        => decimal.Round(part / whole, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The orders in scope, each with what its goods receipts say about it.
    /// </summary>
    /// <remarks>
    /// The receipts are read once for the whole window rather than per order: a
    /// supplier scorecard over a quarter would otherwise issue one query per
    /// order, which is the shape that makes a report time out in production and
    /// nowhere else.
    /// </remarks>
    private async Task<List<Order>> OrdersAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        SupplierId? supplierId,
        CancellationToken cancellationToken)
    {
        IQueryable<PurchaseOrder> query = context.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.Lines)
            .Where(o => o.CreatedAtUtc >= fromUtc && o.CreatedAtUtc <= toUtc);

        if (locations.Count > 0)
        {
            query = query.Where(o => locations.Contains(o.DestinationLocationId));
        }

        if (supplierId is { } supplier)
        {
            query = query.Where(o => o.SupplierId == supplier);
        }

        List<PurchaseOrder> orders = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PurchaseOrderId> orderIds = [.. orders.Select(o => o.Id)];

        List<GoodsReceipt> receipts = await context.GoodsReceipts
            .AsNoTracking()
            .Include(r => r.Lines)
            .Where(r => orderIds.Contains(r.PurchaseOrderId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        ILookup<PurchaseOrderId, GoodsReceipt> byOrder = receipts.ToLookup(r => r.PurchaseOrderId);

        List<SupplierId> supplierIds = [.. orders.Select(o => o.SupplierId).Distinct()];
        var suppliers = await context.Suppliers
            .AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .Select(s => new { Id = s.Id.Value, s.Code, s.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, (string Code, string Name)> supplierNames =
            suppliers.ToDictionary(s => s.Id, s => (s.Code, s.Name));

        List<LocationId> locationIds = [.. orders.Select(o => o.DestinationLocationId).Distinct()];
        var destinations = await context.Locations
            .AsNoTracking()
            .Where(l => locationIds.Contains(l.Id))
            .Select(l => new { Id = l.Id.Value, l.Code })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, string> destinationCodes = destinations.ToDictionary(d => d.Id, d => d.Code);

        return [.. orders.Select(order => Describe(order, byOrder[order.Id], supplierNames, destinationCodes))];
    }

    private static Order Describe(
        PurchaseOrder order,
        IEnumerable<GoodsReceipt> receipts,
        Dictionary<Guid, (string Code, string Name)> suppliers,
        Dictionary<Guid, string> destinations)
    {
        List<GoodsReceipt> booked = [.. receipts];

        (string supplierCode, string supplierName) =
            suppliers.TryGetValue(order.SupplierId.Value, out (string Code, string Name) found)
                ? found
                : (string.Empty, string.Empty);

        return new Order(
            order,
            supplierCode,
            supplierName,
            destinations.TryGetValue(order.DestinationLocationId.Value, out string? code) ? code : string.Empty,

            // The first receipt, not the last: punctuality is about when the goods
            // started arriving, and a trickle of back-orders months later should
            // not rewrite whether the delivery was on time.
            booked.Count == 0 ? null : booked.Min(r => r.ReceivedAtUtc),
            booked.Sum(r => r.Lines.Sum(l => l.QuantityReceived)),
            booked.Sum(r => r.Lines.Sum(l => l.QuantityAccepted)),
            booked.Sum(r => r.Lines.Sum(l =>
                l.QuantityDamaged + l.QuantityWrongItem + l.QuantityExpired + l.OverageBeyondTolerance)),
            booked.Count(r => r.DocumentsMissing),
            booked.Count(r => r.CostVariancePendingApproval));
    }

    private sealed record Order(
        PurchaseOrder Po,
        string SupplierCode,
        string SupplierName,
        string DestinationCode,
        DateTimeOffset? FirstReceivedAtUtc,
        decimal ReceivedQuantity,
        decimal AcceptedQuantity,
        decimal RejectedQuantity,
        int ReceiptsWithMissingDocuments,
        int ReceiptsWithCostVariance);
}
