using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Sales held on the device until they sync, and the catalogue a sale reads,
/// projected from the downloaded cache.
/// </summary>
/// <remarks>
/// <para>
/// It backs the same `CompleteSaleCommandHandler` the server runs (ADR-0008).
/// What made that possible is C37: the handler asks for a `SaleProduct` — the
/// fields a line needs, with the price resolved — rather than a `Product`
/// aggregate, which a device has no category, brand or unit of measure to
/// rebuild.
/// </para>
/// <para>
/// The members that answer questions about returns, refunds and reprints refuse
/// outright. Those use cases are not registered on a device, so reaching one is
/// a registration mistake rather than a runtime condition, and a repository that
/// improvised an answer would hide it.
/// </para>
/// </remarks>
/// <param name="context">The scoped device context.</param>
/// <param name="outbox">The upload queue this repository enqueues to.</param>
public sealed class DeviceSalesRepository(
    PosDeviceDbContext context,
    IDeviceOutbox outbox) : ISalesRepository
{
    /// <inheritdoc />
    public async Task<Result<SaleId>> AddAsync(Sale sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);

        await context.LocalSales.AddAsync(sale, cancellationToken).ConfigureAwait(false);

        await outbox.EnqueueAsync(
            SyncEventType.SaleCompleted,
            new SaleSyncPayload(
                sale.Id.Value,
                sale.EventId.Value,
                sale.Number,
                sale.LocationId.Value,
                sale.DeviceId.Value,
                sale.CashierShiftId.Value,
                sale.CompletedByUserId.Value,
                sale.CustomerId?.Value,
                sale.NetTotal,
                sale.CompletedAtUtc,
                sale.BusinessDate,
                ToLines(sale),
                [.. sale.Payments.Select(p => new SalePaymentSyncPayload(
                    p.Method.ToString(), p.Amount, p.Tendered, p.ProviderReference))]),
            sale.LocationId,
            cancellationToken).ConfigureAwait(false);

        return Result<SaleId>.Success(sale.Id);
    }

    /// <summary>
    /// Rebuilds the lines the cashier rang up from the items the sale stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server replays the sale through the same handler it runs online, and
    /// that handler takes lines, not items: it re-resolves the price, the VAT
    /// class and the FEFO allocation itself. So the event has to carry what was
    /// asked for, not what the device worked out — uploading the device's batch
    /// allocation would be asking head office to trust a shelf it cannot see.
    /// </para>
    /// <para>
    /// One line becomes several items when FEFO splits it across batches, and
    /// every item of a split shares each fact below — the price row included,
    /// since a split is about stock and not about pricing — while differing only
    /// in batch, so grouping restores the line exactly. Two lines identical in all
    /// of them merge into one, which changes how many lines a re-printed receipt
    /// would show and no number the server computes: the quantities and
    /// discounts add up, and a discount spread across a longer line is
    /// apportioned by quantity either way.
    /// </para>
    /// <para>
    /// The expired-batch override is absent because it cannot have happened:
    /// <c>sale.expired_override</c> is not offline-capable, so it never reaches
    /// a device's permission snapshot (OFFLINE_SYNC.md §4).
    /// </para>
    /// </remarks>
    private static IReadOnlyList<SaleLineSyncPayload> ToLines(Sale sale)
        =>
        [
            .. sale.Items
                .OrderBy(i => i.LineNumber)
                .GroupBy(i => new
                {
                    i.ProductId,
                    i.UnitOfMeasureId,
                    i.Barcode,
                    i.PriceWasOverridden,
                    OverriddenPrice = i.PriceWasOverridden ? i.UnitPrice : 0m,
                    i.PriceOverrideAuthorizedByUserId,
                    i.DiscountAuthorizedByUserId,
                    i.PriceVersion,
                })
                .Select(group => new SaleLineSyncPayload(
                    group.Key.ProductId.Value,
                    group.Sum(i => i.Quantity),
                    group.Key.UnitOfMeasureId.Value,
                    group.Key.Barcode,
                    group.Key.PriceWasOverridden ? group.Key.OverriddenPrice : null,
                    group.Key.PriceOverrideAuthorizedByUserId?.Value,
                    group.Sum(i => i.Discount),
                    group.Key.DiscountAuthorizedByUserId?.Value,
                    group.Key.PriceVersion.IsEmpty ? null : group.Key.PriceVersion.Value)),
        ];

    /// <inheritdoc />
    public async Task<Sale?> GetByIdAsync(SaleId saleId, CancellationToken cancellationToken)
        => await context.LocalSales
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Result<SaleId>> UpdateAsync(Sale sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);

        // Tracked by the read that loaded it; the unit of work writes it. The
        // only update a device makes to a sale is voiding it, and the sale's own
        // status is the honest way to know that without a flag threaded through
        // a port the server shares.
        if (sale.Status == SaleStatus.Voided)
        {
            await outbox.EnqueueAsync(
                SyncEventType.SaleVoided,
                new SaleVoidSyncPayload(
                    sale.Id.Value,
                    sale.Number,
                    sale.LocationId.Value,
                    sale.DeviceId.Value,
                    sale.CashierShiftId.Value,
                    sale.BusinessDate,
                    sale.VoidedByUserId?.Value,
                    sale.VoidedAtUtc,
                    sale.VoidReason),
                sale.LocationId,
                cancellationToken).ConfigureAwait(false);
        }

        return Result<SaleId>.Success(sale.Id);
    }

    /// <inheritdoc />
    public async Task<SaleLocationFacts?> GetLocationAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        var cached = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId)
            .Select(l => new { l.Kind, l.SettingsJson })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Settings the feed has not carried read as the strictest configuration.
        return cached is null
            ? null
            : new SaleLocationFacts(cached.Kind, LocationSettings.FromJson(cached.SettingsJson));
    }

    /// <inheritdoc />
    /// <remarks>
    /// A device answers from its own cached price rows. It only ever quotes one
    /// it holds, so this is the same question asked of a smaller catalogue —
    /// and it has to be answered honestly rather than refused, because the
    /// device runs the same handler the server does.
    /// </remarks>
    public async Task<IReadOnlyList<QuotedPrice>> GetQuotedPricesAsync(
        IReadOnlyCollection<ProductPriceId> priceIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(priceIds);

        if (priceIds.Count == 0)
        {
            return [];
        }

        return
        [
            .. await context.ProductPrices
                .AsNoTracking()
                .Where(p => priceIds.Contains(p.Id))
                .Select(p => new QuotedPrice(p.Id, p.ProductId, p.LocationId, p.Amount))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SaleProduct>> GetSaleProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        LocationId locationId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        if (productIds.Count == 0)
        {
            return [];
        }

        List<DeviceCachedProduct> products = await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<DeviceCachedProductPrice> prices = await context.ProductPrices
            .AsNoTracking()
            .Where(p => productIds.Contains(p.ProductId)
                        && p.EffectiveFromUtc <= at
                        && (p.EffectiveToUtc == null || p.EffectiveToUtc > at)
                        && (p.LocationId == null || p.LocationId == locationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. products.Select(product =>
            {
                // The same precedence the aggregate applies: a row scoped to this
                // location beats a global one, and the latest start wins within each.
                DeviceCachedProductPrice? effective = prices
                    .Where(p => p.ProductId == product.Id)
                    .OrderByDescending(p => p.LocationId != null)
                    .ThenByDescending(p => p.EffectiveFromUtc)
                    .FirstOrDefault();

                return new SaleProduct(
                    product.Id,
                    product.Name,
                    product.IsVatExempt,
                    product.TracksBatches,
                    effective?.Id,
                    effective?.Amount);
            }),
        ];
    }

    /// <inheritdoc />
    public async Task<LocationId?> GetExternalCustomerLocationIdAsync(CancellationToken cancellationToken)
        => await context.Locations
            .AsNoTracking()
            .Where(l => l.Kind == Pos.Domain.Locations.LocationKind.External
                        && l.Code == Pos.Domain.Locations.SystemLocationCodes.ExternalCustomer)
            .Select(l => (LocationId?)l.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiredSaleBatch>> GetExpiredAvailableBatchesAsync(
        LocationId locationId,
        ProductId productId,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        DateOnly today = businessDate;

        // Only what the device actually holds: a batch with no local balance is
        // stock this register does not have, whatever the catalogue says.
        return
        [
            .. await (from batch in context.Batches.AsNoTracking()
                      join balance in context.InventoryBalances.AsNoTracking()
                        on batch.Id equals balance.BatchKey
                      where batch.ProductId == productId
                            && balance.LocationId == locationId
                            && balance.ProductId == productId
                            && balance.State == InventoryState.Available
                            && balance.Quantity > 0
                            && batch.ExpiresOn != null
                            && batch.ExpiresOn < today
                      select new ExpiredSaleBatch(
                          batch.Id,
                          batch.LotNumber,
                          balance.Quantity,
                          batch.ExpiresOn,
                          batch.UnitCost))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
        ];
    }

    /// <inheritdoc />
    public async Task<Result<SalesReturnId>> AddReturnAsync(
        SalesReturn salesReturn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(salesReturn);

        await context.LocalSalesReturns.AddAsync(salesReturn, cancellationToken).ConfigureAwait(false);

        // The receipt number, not the row id: head office minted its own sale
        // identifier when it replayed the sale, so the only thing that names the
        // same sale on both sides is the number printed on the customer's copy.
        string? saleNumber = salesReturn.SaleId is { } saleId
            ? await context.LocalSales
                .AsNoTracking()
                .Where(s => s.Id == saleId)
                .Select(s => s.Number)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : null;

        await outbox.EnqueueAsync(
            SyncEventType.SalesReturnCreated,
            new SalesReturnSyncPayload(
                salesReturn.Id.Value,
                salesReturn.Number,
                salesReturn.SaleId?.Value,
                saleNumber,
                salesReturn.LocationId.Value,
                salesReturn.DeviceId.Value,
                salesReturn.CashierShiftId.Value,
                salesReturn.CustomerId?.Value,
                salesReturn.ReturnedByUserId.Value,
                salesReturn.ReturnedAtUtc,
                salesReturn.BusinessDate,
                [.. salesReturn.Items
                    .GroupBy(i => i.ProductId)
                    .Select(g => new SalesReturnLineSyncPayload(g.Key.Value, g.Sum(i => i.Quantity)))]),
            salesReturn.LocationId,
            cancellationToken).ConfigureAwait(false);

        return Result<SalesReturnId>.Success(salesReturn.Id);
    }

    /// <inheritdoc />
    public async Task<SalesReturn?> GetReturnByIdAsync(
        SalesReturnId salesReturnId,
        CancellationToken cancellationToken)
        => await context.LocalSalesReturns
            .Include(r => r.Items)
            .Include(r => r.Refunds)
            .FirstOrDefaultAsync(r => r.Id == salesReturnId, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Result<RefundId>> AddRefundAsync(
        SalesReturnId salesReturnId,
        Refund refund,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refund);

        SalesReturn? salesReturn = await context.LocalSalesReturns
            .FirstOrDefaultAsync(r => r.Id == salesReturnId, cancellationToken)
            .ConfigureAwait(false);

        if (salesReturn is null)
        {
            return Result<RefundId>.Failure(Error.NotFound(
                "sale.return_not_found",
                "The return being refunded does not exist on this device."));
        }

        // Cash leaving the drawer is the thing a shift close has to know about,
        // so it is written locally and reported, never inferred at sync time.
        await context.LocalRefunds.AddAsync(refund, cancellationToken).ConfigureAwait(false);

        await outbox.EnqueueAsync(
            SyncEventType.RefundIssued,
            new RefundSyncPayload(
                refund.Id.Value,
                salesReturnId.Value,
                salesReturn.Number,
                salesReturn.LocationId.Value,
                refund.DeviceId.Value,
                refund.CashierShiftId.Value,
                refund.Method.ToString(),
                refund.Amount,
                refund.Tendered,
                refund.ProviderReference,
                refund.RefundedAtUtc,
                refund.RefundedByUserId.Value),
            salesReturn.LocationId,
            cancellationToken).ConfigureAwait(false);

        return Result<RefundId>.Success(refund.Id);
    }

    /// <inheritdoc />
    public async Task<Refund?> GetRefundByEventAsync(EventId eventId, CancellationToken cancellationToken)
        => await context.LocalRefunds
            .FirstOrDefaultAsync(r => r.EventId == eventId, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Result<ReceiptPrintId>> AddReceiptPrintAsync(
        SaleReceiptPrint print,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(print);

        await context.LocalReceiptPrints.AddAsync(print, cancellationToken).ConfigureAwait(false);

        // A reprint is an audited act, not a display concern: someone can walk
        // out with a second copy of a receipt, so head office is told.
        Sale? sale = await context.LocalSales
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == print.SaleId, cancellationToken)
            .ConfigureAwait(false);

        if (sale is not null)
        {
            await outbox.EnqueueAsync(
                SyncEventType.SaleReceiptReprinted,
                new ReceiptPrintSyncPayload(
                    print.Id.Value,
                    print.SaleId.Value,
                    sale.Number,
                    sale.LocationId.Value,
                    sale.DeviceId.Value,
                    print.PrintedByUserId.Value,
                    print.PrintedAtUtc,
                    print.IsReprint,
                    print.Reason),
                sale.LocationId,
                cancellationToken).ConfigureAwait(false);
        }

        return Result<ReceiptPrintId>.Success(print.Id);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Product>> GetReturnProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetReturnProductsAsync));

    private static NotSupportedException NotOnADeviceYet(string member)
        => new(FormattableString.Invariant(
            $"{member} belongs to a use case that is not registered on a device; reaching it is a registration mistake, not a runtime condition."));
}

/// <summary>
/// What the server is told about a sale that happened offline: what the cashier
/// rang up, not what the device worked out from it.
/// </summary>
/// <param name="SaleId">The sale the device created.</param>
/// <param name="EventId">
/// The business event the device generated when the sale was rung up. The
/// server passes it back into the ledger, which is what makes a replayed sale
/// post its movements once.
/// </param>
/// <param name="Number">The device-scoped SAL number printed on the receipt.</param>
/// <param name="LocationId">Where it was sold.</param>
/// <param name="DeviceId">The register.</param>
/// <param name="ShiftId">The shift it belongs to.</param>
/// <param name="CompletedByUserId">The cashier who completed it.</param>
/// <param name="CustomerId">The named customer, when there was one.</param>
/// <param name="NetTotal">
/// What was actually taken, as printed on the receipt. The server re-derives
/// its own from the prices it holds; this is sent so a difference can be shown
/// on the price-variance report rather than silently re-priced.
/// </param>
/// <param name="CompletedAtUtc">The device clock at completion.</param>
/// <param name="BusinessDate">The business date in the location's timezone.</param>
/// <param name="Lines">What was sold, as the cashier asked for it.</param>
/// <param name="Payments">How it was settled.</param>
public sealed record SaleSyncPayload(
    Guid SaleId,
    Guid EventId,
    string Number,
    Guid LocationId,
    Guid DeviceId,
    Guid ShiftId,
    Guid CompletedByUserId,
    Guid? CustomerId,
    decimal NetTotal,
    DateTimeOffset CompletedAtUtc,
    DateOnly BusinessDate,
    IReadOnlyList<SaleLineSyncPayload> Lines,
    IReadOnlyList<SalePaymentSyncPayload> Payments);

/// <summary>One line of a sale rung up offline.</summary>
/// <param name="ProductId">The product sold.</param>
/// <param name="Quantity">How much, in the unit of measure.</param>
/// <param name="UnitOfMeasureId">The unit the quantity is expressed in.</param>
/// <param name="Barcode">The scanned barcode, when it was scanned.</param>
/// <param name="UnitPriceOverride">The cashier-entered price, when one was entered.</param>
/// <param name="PriceOverrideAuthorizedByUserId">Who authorized that override.</param>
/// <param name="Discount">The manual discount on the line.</param>
/// <param name="DiscountAuthorizedByUserId">Who authorized the discount.</param>
/// <param name="QuotedPriceVersion">
/// The price row the device charged from, so head office records the number the
/// customer agreed to pay even when that row has since been superseded. The
/// amount is deliberately not sent: the server reads it back from the row.
/// </param>
public sealed record SaleLineSyncPayload(
    Guid ProductId,
    decimal Quantity,
    Guid UnitOfMeasureId,
    string? Barcode,
    decimal? UnitPriceOverride,
    Guid? PriceOverrideAuthorizedByUserId,
    decimal Discount,
    Guid? DiscountAuthorizedByUserId,
    Guid? QuotedPriceVersion = null);

/// <summary>One payment that settled a sale rung up offline.</summary>
/// <param name="Method">The payment method, by name.</param>
/// <param name="Amount">The amount applied to the sale.</param>
/// <param name="Tendered">What was handed over, for cash.</param>
/// <param name="ProviderReference">The provider reference, for card and wallet.</param>
public sealed record SalePaymentSyncPayload(
    string Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference);

/// <summary>What the server is told about a sale voided offline.</summary>
/// <param name="SaleId">The sale.</param>
/// <param name="Number">The receipt number the void applies to.</param>
/// <param name="LocationId">Where it was sold.</param>
/// <param name="DeviceId">The register that voided it.</param>
/// <param name="ShiftId">The shift the void belongs to, which must still be open.</param>
/// <param name="BusinessDate">The business date the reversal counts toward.</param>
/// <param name="VoidedByUserId">Who voided it.</param>
/// <param name="VoidedAtUtc">When, by the device clock.</param>
/// <param name="Reason">Why, which a void always requires.</param>
public sealed record SaleVoidSyncPayload(
    Guid SaleId,
    string Number,
    Guid LocationId,
    Guid DeviceId,
    Guid ShiftId,
    DateOnly BusinessDate,
    Guid? VoidedByUserId,
    DateTimeOffset? VoidedAtUtc,
    string? Reason);

/// <summary>What the server is told about a receipt printed offline.</summary>
/// <param name="PrintId">The print record.</param>
/// <param name="SaleId">The sale it belongs to.</param>
/// <param name="Number">The receipt number.</param>
/// <param name="LocationId">Where the copy came out.</param>
/// <param name="DeviceId">The register that printed it.</param>
/// <param name="PrintedByUserId">Who printed it.</param>
/// <param name="PrintedAtUtc">When, by the device clock.</param>
/// <param name="IsReprint">Whether this was a second copy.</param>
/// <param name="Reason">Why a reprint was needed.</param>
public sealed record ReceiptPrintSyncPayload(
    Guid PrintId,
    Guid SaleId,
    string Number,
    Guid LocationId,
    Guid DeviceId,
    Guid PrintedByUserId,
    DateTimeOffset PrintedAtUtc,
    bool IsReprint,
    string? Reason);

/// <summary>What the server is told about a return taken offline.</summary>
/// <remarks>
/// Like the sale, it carries what the cashier accepted — the products and the
/// quantities — and not the device's valuation of them. The server matches each
/// product against the original sale's lines and re-derives the price, the VAT
/// and the refundable amount from the snapshots it holds, so a refund is never
/// paid out against a figure a register worked out for itself.
/// </remarks>
/// <param name="ReturnId">The return, as the device numbered it internally.</param>
/// <param name="Number">The RET number printed at the till.</param>
/// <param name="SaleId">The device's identifier for the sale, kept for its own records.</param>
/// <param name="SaleNumber">
/// The SAL number of the sale being returned against. This is what the server
/// resolves, because it minted its own identifier when it replayed the sale.
/// </param>
/// <param name="LocationId">Where the goods came back.</param>
/// <param name="DeviceId">The register that took them.</param>
/// <param name="ShiftId">The shift that accepted them.</param>
/// <param name="CustomerId">The named customer, when there was one.</param>
/// <param name="ReturnedByUserId">The cashier who accepted the goods.</param>
/// <param name="ReturnedAtUtc">The device clock at acceptance.</param>
/// <param name="BusinessDate">The business date in the location's timezone.</param>
/// <param name="Lines">What came back, and how much of it.</param>
public sealed record SalesReturnSyncPayload(
    Guid ReturnId,
    string Number,
    Guid? SaleId,
    string? SaleNumber,
    Guid LocationId,
    Guid DeviceId,
    Guid ShiftId,
    Guid? CustomerId,
    Guid ReturnedByUserId,
    DateTimeOffset ReturnedAtUtc,
    DateOnly BusinessDate,
    IReadOnlyList<SalesReturnLineSyncPayload> Lines);

/// <summary>One product coming back over the counter.</summary>
/// <param name="ProductId">What came back.</param>
/// <param name="Quantity">How much of it.</param>
public sealed record SalesReturnLineSyncPayload(Guid ProductId, decimal Quantity);

/// <summary>What the server is told about cash that went back to a customer.</summary>
/// <param name="RefundId">The refund, as the device numbered it internally.</param>
/// <param name="ReturnId">The device's identifier for the return it settles.</param>
/// <param name="ReturnNumber">
/// That return's RET number, which is what the server resolves: it minted its
/// own identifier when it replayed the return.
/// </param>
/// <param name="LocationId">Where the money went back over the counter.</param>
/// <param name="DeviceId">The register that paid it out.</param>
/// <param name="ShiftId">The shift whose drawer it came out of.</param>
/// <param name="Method">How it was paid back.</param>
/// <param name="Amount">How much.</param>
/// <param name="Tendered">What was handed over, for cash.</param>
/// <param name="ProviderReference">The provider reference, for card and wallet.</param>
/// <param name="RefundedAtUtc">The device clock at payout.</param>
/// <param name="RefundedByUserId">The cashier who paid it out.</param>
public sealed record RefundSyncPayload(
    Guid RefundId,
    Guid ReturnId,
    string ReturnNumber,
    Guid LocationId,
    Guid DeviceId,
    Guid ShiftId,
    string Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference,
    DateTimeOffset RefundedAtUtc,
    Guid RefundedByUserId);
