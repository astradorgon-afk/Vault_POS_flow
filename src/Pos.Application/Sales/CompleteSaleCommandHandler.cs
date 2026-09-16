using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Inventory;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Handles <see cref="CompleteSaleCommand"/>. The handler re-derives every
/// business fact — the effective price, the VAT classification, the FEFO
/// allocation and the cash rounding — from server-authoritative data, verifies
/// the shift is open and owned by the posting device and cashier, and creates
/// the sale under the device-allocated SAL number, then posts the inventory
/// movements and writes the audit entry, all inside the unit-of-work
/// transaction.
/// </summary>
/// <remarks>
/// <para>
/// Idempotency is achieved by passing the device-generated
/// <see cref="CompleteSaleCommand.EventId"/> into the ledger: a second post
/// with the same event replays the first outcome and the sale is already
/// written.
/// </para>
/// <para>
/// The SAL number is device-scoped (POS.md §11): the device allocates it when
/// the sale is rung up, so an offline sale keeps the number printed on its
/// receipt. The server only verifies that the number's device code matches the
/// device posting it — it never allocates a device-scoped number itself.
/// </para>
/// </remarks>
public sealed class CompleteSaleCommandHandler(
    ISalesRepository repository,
    ICustomerRepository customers,
    IExpiryService expiry,
    IInventoryLedger ledger,
    IShiftRepository shifts,
    IPermissionEvaluator permissions,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<CompleteSaleCommand, SaleId>
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public async Task<Result<SaleId>> HandleAsync(
        CompleteSaleCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ------------------------------------------------------------------
        // 1. Load location facts.
        // ------------------------------------------------------------------
        SaleLocationFacts? location = await repository
            .GetLocationAsync(command.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.LocationUnknown(command.LocationId));
        }

        if (location.Kind == LocationKind.External)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.LocationExternal);
        }

        if (location.Settings.VatRate <= 0m)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.VatRateInvalid(command.LocationId));
        }

        // ------------------------------------------------------------------
        // 1b. Verify the device-allocated SAL number and the shift state.
        // ------------------------------------------------------------------
        if (command.Number.DeviceShortCode is null)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.NumberInvalid);
        }

        ShiftDeviceFacts? device = await shifts
            .GetDeviceFactsAsync(command.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.DeviceUnknown(command.DeviceId));
        }

        if (!string.Equals(command.Number.DeviceShortCode, device.ShortCode, StringComparison.OrdinalIgnoreCase))
        {
            return Result<SaleId>.Failure(SaleCommandErrors.NumberDeviceMismatch);
        }

        CashierShift? shift = await shifts
            .GetShiftAsync(command.CashierShiftId, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.ShiftUnknown(command.CashierShiftId));
        }

        if (shift.Status != ShiftStatus.Open)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.ShiftNotOpen(shift.Status));
        }

        if (shift.CashierUserId != command.CashierId)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.ShiftCashierMismatch);
        }

        if (shift.DeviceId != command.DeviceId)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.ShiftDeviceMismatch);
        }

        // ------------------------------------------------------------------
        // 1c. Resolve the customer reference, when one is provided.
        // ------------------------------------------------------------------
        if (command.CustomerId is { } customerId)
        {
            Customer? customer = await customers
                .GetByIdAsync(customerId, cancellationToken)
                .ConfigureAwait(false);

            if (customer is null)
            {
                return Result<SaleId>.Failure(SaleCommandErrors.CustomerUnknown(customerId));
            }

            if (!customer.IsActive)
            {
                return Result<SaleId>.Failure(SaleCommandErrors.CustomerInactive(customerId));
            }
        }

        // ------------------------------------------------------------------
        // 2. Load every product being sold, with its price rows.
        // ------------------------------------------------------------------
        ProductId[] productIds = [.. command.Lines.Select(l => l.ProductId).Distinct()];
        IReadOnlyList<Product> products = await repository
            .GetSaleProductsAsync(productIds, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<ProductId, Product> productById = products.ToDictionary(p => p.Id);

        foreach (ProductId productId in productIds)
        {
            if (!productById.ContainsKey(productId))
            {
                return Result<SaleId>.Failure(SaleCommandErrors.ProductUnknown(productId));
            }
        }

        // ------------------------------------------------------------------
        // 3. Pre-cache which authorizers hold discount / override permission.
        // ------------------------------------------------------------------
        HashSet<UserId> discountAuthorized = [];
        HashSet<UserId> priceOverrideAuthorized = [];

        foreach (CompleteSaleLine line in command.Lines)
        {
            if (line.Discount > 0m && line.DiscountAuthorizedByUserId is { } da && discountAuthorized.Add(da))
            {
                if (!await permissions
                        .HasPermissionAsync(da, Permissions.Sales.Discount, command.LocationId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return Result<SaleId>.Failure(SaleCommandErrors.DiscountNotAuthorized);
                }
            }

            if (line.UnitPriceOverride is not null && line.PriceOverrideAuthorizedByUserId is { } poa && priceOverrideAuthorized.Add(poa))
            {
                if (!await permissions
                        .HasPermissionAsync(poa, Permissions.Sales.PriceOverride, command.LocationId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return Result<SaleId>.Failure(SaleCommandErrors.PriceOverrideNotAuthorized);
                }
            }
        }

        // Pre-cache the expired-override permission for the cashier.
        bool hasExpiredOverridePermission = await permissions
            .HasPermissionAsync(command.CashierId, Permissions.Sales.ExpiredOverride, command.LocationId, cancellationToken)
            .ConfigureAwait(false);

        // ------------------------------------------------------------------
        // 4. Resolve each line: price, VAT, FEFO, split multi-slice.
        // ------------------------------------------------------------------
        List<ItemSpec> itemSpecs = [];
        List<PaymentSpec> paymentSpecs = new(command.Payments.Count);

        foreach (CompleteSalePayment payment in command.Payments)
        {
            paymentSpecs.Add(new PaymentSpec(payment.Method, payment.Amount, payment.Tendered, payment.ProviderReference));
        }

        foreach (CompleteSaleLine line in command.Lines)
        {
            Product product = productById[line.ProductId];

            // Asking for the exception path itself is a permission use even when
            // the sellable shelf ends up covering the line: a cashier without
            // the authority cannot offer to sell expired stock.
            if (line.AllowExpiredOverride && !hasExpiredOverridePermission)
            {
                return Result<SaleId>.Failure(SaleCommandErrors.ExpiredOverrideDenied);
            }

            // --- 4a. Price resolution. ---
            ProductPrice? effectivePrice = product.PriceAt(command.LocationId, command.CompletedAtUtc);

            decimal unitPrice;
            ProductPriceId priceVersion;
            bool priceWasOverridden = line.UnitPriceOverride is not null;

            if (line.UnitPriceOverride is { } overrideValue)
            {
                unitPrice = overrideValue;
                // Still record which row *would* have been effective, or Empty
                // when no price row exists yet for the product.
                priceVersion = effectivePrice?.Id ?? ProductPriceId.Empty;
            }
            else
            {
                if (effectivePrice is null)
                {
                    return Result<SaleId>.Failure(SaleCommandErrors.PriceMissing(line.ProductId, command.LocationId));
                }

                unitPrice = effectivePrice.Price.Amount;
                priceVersion = effectivePrice.Id;
            }

            // --- 4b. VAT classification. ---
            decimal? vatRate;
            bool isVatExempt;
            bool isZeroRated = false; // No Product flag for zero-rated yet (POS.md §2.6)

            if (product.IsVatExempt)
            {
                vatRate = null;
                isVatExempt = true;
            }
            else
            {
                vatRate = location.Settings.VatRate;
                isVatExempt = false;
            }

            // --- 4c. FEFO allocation. ---
            IReadOnlyList<SellableBatchItem> sellable = await expiry
                .GetSellableBatchesAsync(command.LocationId, line.ProductId, cancellationToken)
                .ConfigureAwait(false);

            List<AllocatableBatch> allocatableBatches =
            [
                .. sellable.Select(b => new AllocatableBatch(
                    b.BatchId.IsEmpty ? null : b.BatchId,
                    string.IsNullOrWhiteSpace(b.LotNumber) ? null : b.LotNumber,
                    b.Quantity,
                    b.UnitCost,
                    b.ExpiresOn)),
            ];

            Result<IReadOnlyList<AllocatedSlice>> allocation = FefoBatches.Allocate(
                line.ProductId,
                command.LocationId,
                line.Quantity,
                allocatableBatches);

            decimal sellableTotal = allocatableBatches.Sum(b => b.Quantity);
            bool expiredUsed = false;

            if (allocation.IsFailure && line.AllowExpiredOverride && hasExpiredOverridePermission)
            {
                // The sellable shelf alone cannot cover the line, the cashier
                // asked for the exception path and holds the permission for it.
                // Take every unit the sellable shelf can provide, then cover the
                // remainder from expired batches.
                IReadOnlyList<ExpiredSaleBatch> expiredBatches = await repository
                    .GetExpiredAvailableBatchesAsync(
                        command.LocationId,
                        line.ProductId,
                        command.BusinessDate,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (expiredBatches.Count > 0)
                {
                    List<AllocatableBatch> expiredAllocatable =
                    [
                        .. expiredBatches.Select(b => new AllocatableBatch(
                            b.BatchId,
                            b.LotNumber,
                            b.Quantity,
                            b.UnitCost,
                            b.ExpiresOn)),
                    ];

                    // Allocate every sellable unit — guaranteed to succeed
                    // since the sum matches or exceeds what the batches hold.
                    Result<IReadOnlyList<AllocatedSlice>> sellableAllocation = FefoBatches.Allocate(
                        line.ProductId,
                        command.LocationId,
                        sellableTotal,
                        allocatableBatches);

                    decimal remaining = line.Quantity - sellableTotal;

                    if (sellableAllocation.IsSuccess && remaining > 0m)
                    {
                        Result<IReadOnlyList<AllocatedSlice>> expiredAllocation = FefoBatches.Allocate(
                            line.ProductId,
                            command.LocationId,
                            remaining,
                            expiredAllocatable);

                        if (expiredAllocation.IsSuccess)
                        {
                            allocation = Result<IReadOnlyList<AllocatedSlice>>.Success(
                                [.. sellableAllocation.Value, .. expiredAllocation.Value]);
                            expiredUsed = true;
                        }
                    }
                    else if (sellableAllocation.IsSuccess && remaining <= 0m)
                    {
                        // The sellable shelf actually covers the full request
                        // (primary failed only because of rounding). This
                        // shouldn't normally happen, but be defensive.
                        allocation = sellableAllocation;
                    }
                }

                // When the expired batch also cannot cover the request, keep
                // the primary allocation's failure so the reported shortfall is
                // the sellable quantity, not the expired one.
            }

            if (allocation.IsFailure)
            {
                // The sellable shelf cannot cover the line. When the override
                // path was not available, distinguish "only expired stock
                // remains" (POS.md §5, inventory.expired_only) from a genuine
                // shortfall so the terminal can offer the exception path.
                decimal shortfall = line.Quantity - sellableTotal;

                if (shortfall > 0m)
                {
                    IReadOnlyList<ExpiredSaleBatch> expiredProbe = await repository
                        .GetExpiredAvailableBatchesAsync(
                            command.LocationId,
                            line.ProductId,
                            command.BusinessDate,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (expiredProbe.Sum(b => b.Quantity) >= shortfall)
                    {
                        return Result<SaleId>.Failure(
                            SaleErrors.ExpiredOnly(line.ProductId, command.LocationId));
                    }
                }

                return Result<SaleId>.Failure(allocation.Errors);
            }

            if (expiredUsed)
            {
                // Record the override for investigation. Written once per line
                // that drew on an expired batch, not once per slice. The acting
                // user is stamped by the audit writer from the request context;
                // the reason the cashier supplied is carried alongside so the
                // expiry exception report never has to guess why a sale ran.
                await audit.WriteAsync(new AuditEntry(
                    AuditActions.Sales.ExpiredOverride,
                    "sale_line",
                    line.ProductId.Value,
                    Reason: line.ExpiredOverrideReason,
                    NewValueJson: JsonSerializer.Serialize(
                        new
                        {
                            ProductId = line.ProductId.Value,
                            LocationId = command.LocationId.Value,
                            Quantity = line.Quantity,
                            AuthorizingUserId = command.CashierId.Value,
                        },
                        JsonDefaults),
                    LocationId: command.LocationId),
                    cancellationToken).ConfigureAwait(false);
            }

            // --- 4d. Split allocation slices into one ItemSpec each. The
            // --- discount is a whole-line fact; apportion it across slices by
            // --- quantity, absorbing the rounding remainder in the last slice
            // --- so the slices' discounts always sum back to the line total.
            decimal discountRemaining = line.Discount;
            IReadOnlyList<AllocatedSlice> slices = allocation.Value;

            for (int index = 0; index < slices.Count; index++)
            {
                AllocatedSlice slice = slices[index];

                decimal sliceDiscount = index == slices.Count - 1
                    ? discountRemaining
                    : decimal.Round(
                        line.Discount * (slice.Quantity / line.Quantity),
                        Money.StorageScale,
                        Money.IntermediateRounding);

                discountRemaining -= sliceDiscount;

                itemSpecs.Add(new ItemSpec(
                    line.ProductId,
                    product.Name,
                    line.Barcode,
                    slice.Quantity,
                    line.UnitOfMeasureId,
                    unitPrice,
                    priceVersion,
                    priceWasOverridden,
                    line.PriceOverrideAuthorizedByUserId,
                    sliceDiscount,
                    line.DiscountAuthorizedByUserId,
                    vatRate,
                    isVatExempt,
                    isZeroRated,
                    slice.BatchId,
                    slice.BatchCode,
                    slice.ExpiresOn,
                    slice.UnitCost,
                    product.TracksBatches));
            }
        }

        // ------------------------------------------------------------------
        // 5. Create the sale via the domain factory, under the device-allocated
        //    SAL number verified in step 1b.
        // ------------------------------------------------------------------
        Result<Sale> created = Sale.Create(
            command.Number,
            command.EventId,
            command.LocationId,
            command.CashierShiftId,
            command.DeviceId,
            command.CustomerId,
            command.BusinessDate,
            command.CompletedAtUtc,
            command.CashierId,
            itemSpecs,
            paymentSpecs,
            location.Settings.CashRoundingIncrement);

        if (created.IsFailure)
        {
            return Result<SaleId>.Failure(created.Errors);
        }

        Sale sale = created.Value;

        // ------------------------------------------------------------------
        // 6. Post the PosSale ledger movement (store Available ↔ EXT-CUSTOMER).
        // ------------------------------------------------------------------
        LocationId? externalCustomerLocationId = await repository
            .GetExternalCustomerLocationIdAsync(cancellationToken)
            .ConfigureAwait(false);

        if (externalCustomerLocationId is null)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.ExternalCustomerLocationMissing);
        }

        List<MovementLegSpec> legs = [];

        foreach (SaleItem item in sale.Items)
        {
            bool tracksBatches = productById[item.ProductId].TracksBatches;

            // Negative leg: store's Available bucket.
            legs.Add(new MovementLegSpec(
                item.ProductId,
                item.BatchId,
                command.LocationId,
                location.Kind,
                InventoryState.Available,
                -item.Quantity,
                item.UnitCost,
                tracksBatches));

            // Positive leg: EXT-CUSTOMER's External bucket.
            legs.Add(new MovementLegSpec(
                item.ProductId,
                item.BatchId,
                externalCustomerLocationId.Value,
                LocationKind.External,
                InventoryState.External,
                +item.Quantity,
                item.UnitCost,
                tracksBatches));
        }

        MovementGroupSpec movementGroup = new(
            EventId: command.EventId,
            MovementType: InventoryMovementType.PosSale,
            ReferenceDocumentType: ReferenceDocumentType.Sale,
            ReferenceDocumentId: sale.Id.Value,
            ReferenceNumber: sale.Number,
            Legs: legs,
            Actor: new LedgerActor(
                CreatedBy: command.CashierId,
                ApprovedBy: null,
                Device: currentUser.DeviceId,
                Correlation: currentUser.CorrelationId),
            OccurredAtUtc: command.CompletedAtUtc,
            BusinessDate: command.BusinessDate);

        Result<PostedMovementGroup> posted = await ledger
            .PostAsync(movementGroup, cancellationToken)
            .ConfigureAwait(false);

        if (posted.IsFailure)
        {
            return Result<SaleId>.Failure(posted.Errors);
        }

        // ------------------------------------------------------------------
        // 7. Audit the sale completion.
        // ------------------------------------------------------------------
        string auditJson = JsonSerializer.Serialize(new
        {
            sale.Id,
            sale.Number,
            LocationId = command.LocationId.Value,
            sale.NetTotal,
            sale.VatTotal,
            sale.DiscountTotal,
            CashierId = sale.CompletedByUserId.Value,
            LineCount = sale.Items.Count,
            PaymentCount = sale.Payments.Count,
        }, JsonDefaults);

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.SaleCompleted,
            "sale",
            sale.Id.Value,
            NewValueJson: auditJson,
            ReferenceDocumentType: ReferenceDocumentType.Sale,
            ReferenceDocumentId: sale.Id.Value,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        // ------------------------------------------------------------------
        // 8. Persist the sale.
        // ------------------------------------------------------------------
        return await repository
            .AddAsync(sale, cancellationToken)
            .ConfigureAwait(false);
    }
}