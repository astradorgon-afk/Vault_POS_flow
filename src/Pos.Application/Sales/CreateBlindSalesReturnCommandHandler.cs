using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Handles <see cref="CreateBlindSalesReturnCommand"/>. A blind return accepts
/// goods back with no original sale (POS.md §4), so there is no sale whose facts
/// can be verified and no proportional snapshot to freeze. The handler instead
/// verifies the device-allocated RET number against the authenticated device and
/// the open shift, resolves every line's current selling price, VAT class and
/// valuation cost from the catalog at the return's location, then lets the
/// aggregate freeze its snapshot. It posts the same CustomerReturn ledger
/// movement a referenced return would — EXT-CUSTOMER's External bucket gives up
/// every unit and the store's ReturnPending bucket receives them — and writes
/// the <c>sale.return.created</c> audit plus a mandatory
/// <c>sale.return.blind.accepted</c> exception record with the reason. All of it
/// commits in the unit-of-work transaction.
/// </summary>
/// <remarks>
/// The External bucket is deliberately unbounded: the ledger's availability check
/// never probes it, so a blind acceptance that runs the customer's bucket
/// negative is posted rather than refused. The exception record is what makes
/// that bounded: every blind acceptance is visible to HQ under its reason, and a
/// refund against it later remains capped by the accepted value.
/// </remarks>
public sealed class CreateBlindSalesReturnCommandHandler(
    ISalesRepository repository,
    IShiftRepository shifts,
    IInventoryLedger ledger,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<CreateBlindSalesReturnCommand, SalesReturnId>
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public async Task<Result<SalesReturnId>> HandleAsync(
        CreateBlindSalesReturnCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ------------------------------------------------------------------
        // 1. Verify the device-allocated RET number's device code matches,
        //    and verify the shift is open on the same device.
        // ------------------------------------------------------------------
        if (command.Number.DeviceShortCode is null)
        {
            return Result<SalesReturnId>.Failure(ReturnCommandErrors.NumberInvalid);
        }

        ShiftDeviceFacts? device = await shifts
            .GetDeviceFactsAsync(command.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Result<SalesReturnId>.Failure(SaleCommandErrors.DeviceUnknown(command.DeviceId));
        }

        if (!string.Equals(command.Number.DeviceShortCode, device.ShortCode, StringComparison.OrdinalIgnoreCase))
        {
            return Result<SalesReturnId>.Failure(ReturnCommandErrors.NumberDeviceMismatch);
        }

        CashierShift? shift = await shifts
            .GetShiftAsync(command.ShiftId, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return Result<SalesReturnId>.Failure(SaleCommandErrors.ShiftUnknown(command.ShiftId));
        }

        if (shift.Status != ShiftStatus.Open)
        {
            return Result<SalesReturnId>.Failure(SaleCommandErrors.ShiftNotOpen(shift.Status));
        }

        if (shift.DeviceId != command.DeviceId)
        {
            return Result<SalesReturnId>.Failure(SaleCommandErrors.ShiftDeviceMismatch);
        }

        // ------------------------------------------------------------------
        // 2. Load the location facts and the products' catalog state, then
        //    resolve every line's price, VAT class and valuation cost.
        // ------------------------------------------------------------------
        SaleLocationFacts? location = await repository
            .GetLocationAsync(command.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return Result<SalesReturnId>.Failure(SaleCommandErrors.LocationUnknown(command.LocationId));
        }

        List<BlindSalesReturnLine> lines = command.Lines.ToList();

        IReadOnlyList<Product> products = await repository
            .GetReturnProductsAsync(lines.Select(l => l.ProductId).Distinct().ToArray(), cancellationToken)
            .ConfigureAwait(false);

        Dictionary<ProductId, Product> productById = products.ToDictionary(p => p.Id);

        List<BlindReturnItemSpec> specs = [];

        foreach (BlindSalesReturnLine line in lines)
        {
            if (!productById.TryGetValue(line.ProductId, out Product? product))
            {
                return Result<SalesReturnId>.Failure(BlindReturnCommandErrors.ProductUnknown(line.ProductId));
            }

            if (line.Quantity <= 0m)
            {
                return Result<SalesReturnId>.Failure(SalesReturnErrors.QuantityInvalid);
            }

            ProductPrice? effectivePrice = product.PriceAt(command.LocationId, command.ReturnedAtUtc);

            if (effectivePrice is null)
            {
                return Result<SalesReturnId>.Failure(
                    BlindReturnCommandErrors.PriceMissing(line.ProductId, command.LocationId));
            }

            decimal? vatRate;
            bool isVatExempt;

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

            string? barcode = product.Barcodes
                .FirstOrDefault(b => b.IsPrimary && b.RetiredAtUtc is null)
                ?.Value;

            specs.Add(new BlindReturnItemSpec(
                line.ProductId,
                product.Name,
                barcode,
                line.Quantity,
                product.BaseUnitOfMeasureId,
                effectivePrice.Price.Amount,
                vatRate,
                isVatExempt,
                IsZeroRated: false,
                product.DefaultPurchaseCost));
        }

        // ------------------------------------------------------------------
        // 3. Create the blind return. The aggregate enforces the mandatory
        //    exception reason and freezes the resolved valuations.
        // ------------------------------------------------------------------
        Result<SalesReturn> created = SalesReturn.CreateBlind(
            command.Number,
            command.EventId,
            command.LocationId,
            command.ShiftId,
            command.DeviceId,
            command.CustomerId,
            command.BusinessDate,
            command.ReturnedAtUtc,
            command.ReturnedByUserId,
            command.Reason,
            specs);

        if (created.IsFailure)
        {
            return Result<SalesReturnId>.Failure(created.Errors);
        }

        SalesReturn salesReturn = created.Value;

        // ------------------------------------------------------------------
        // 4. Post the CustomerReturn ledger movement: EXT-CUSTOMER's External
        //    bucket gives up every returned unit and the store's ReturnPending
        //    bucket receives them.
        // ------------------------------------------------------------------
        LocationId? externalCustomerLocationId = await repository
            .GetExternalCustomerLocationIdAsync(cancellationToken)
            .ConfigureAwait(false);

        if (externalCustomerLocationId is null)
        {
            return Result<SalesReturnId>.Failure(SaleCommandErrors.ExternalCustomerLocationMissing);
        }

        List<MovementLegSpec> legs = [];

        foreach (SalesReturnItem item in salesReturn.Items)
        {
            // Positive leg: store's ReturnPending bucket receives the goods.
            legs.Add(new MovementLegSpec(
                item.ProductId,
                BatchId: null,
                command.LocationId,
                location.Kind,
                InventoryState.ReturnPending,
                +item.Quantity,
                item.UnitCost,
                ProductTracksBatches: false));

            // Negative leg: EXT-CUSTOMER's External bucket gives them up.
            legs.Add(new MovementLegSpec(
                item.ProductId,
                BatchId: null,
                externalCustomerLocationId.Value,
                LocationKind.External,
                InventoryState.External,
                -item.Quantity,
                item.UnitCost,
                ProductTracksBatches: false));
        }

        MovementGroupSpec movementGroup = new(
            EventId: command.EventId,
            MovementType: InventoryMovementType.CustomerReturn,
            ReferenceDocumentType: ReferenceDocumentType.SalesReturn,
            ReferenceDocumentId: salesReturn.Id.Value,
            ReferenceNumber: salesReturn.Number,
            Legs: legs,
            Actor: new LedgerActor(
                CreatedBy: command.ReturnedByUserId,
                ApprovedBy: null,
                Device: currentUser.DeviceId,
                Correlation: currentUser.CorrelationId),
            OccurredAtUtc: command.ReturnedAtUtc,
            BusinessDate: command.BusinessDate);

        Result<PostedMovementGroup> posted = await ledger
            .PostAsync(movementGroup, cancellationToken)
            .ConfigureAwait(false);

        if (posted.IsFailure)
        {
            return Result<SalesReturnId>.Failure(posted.Errors);
        }

        // ------------------------------------------------------------------
        // 5. Audit the return and the exception record.
        // ------------------------------------------------------------------
        string auditJson = JsonSerializer.Serialize(new
        {
            salesReturn.Id,
            Number = salesReturn.Number,
            LocationId = command.LocationId.Value,
            salesReturn.RefundableTotal,
            ReturnedByUserId = command.ReturnedByUserId.Value,
        }, JsonDefaults);

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ReturnCreated,
            "sales_return",
            salesReturn.Id.Value,
            NewValueJson: auditJson,
            ReferenceDocumentType: ReferenceDocumentType.SalesReturn,
            ReferenceDocumentId: salesReturn.Id.Value,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        string blindJson = JsonSerializer.Serialize(new
        {
            salesReturn.Id,
            Number = salesReturn.Number,
            LocationId = command.LocationId.Value,
            Reason = command.Reason,
            ReturnedByUserId = command.ReturnedByUserId.Value,
        }, JsonDefaults);

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.BlindReturnAccepted,
            "sales_return",
            salesReturn.Id.Value,
            NewValueJson: blindJson,
            Reason: command.Reason,
            ReferenceDocumentType: ReferenceDocumentType.SalesReturn,
            ReferenceDocumentId: salesReturn.Id.Value,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        // ------------------------------------------------------------------
        // 6. Persist the new return.
        // ------------------------------------------------------------------
        return await repository
            .AddReturnAsync(salesReturn, cancellationToken)
            .ConfigureAwait(false);
    }
}