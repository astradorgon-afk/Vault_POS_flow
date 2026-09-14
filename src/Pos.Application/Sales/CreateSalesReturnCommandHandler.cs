using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Handles <see cref="CreateSalesReturnCommand"/>. The handler verifies that the
/// sale exists, belongs to the same location and device, and still holds
/// returnable units, then performs first-come first-served FIFO allocation of
/// each requested product across the sale's lines in line-number order (so
/// earlier batch slices are returned first, matching the FEFO sell path). It
/// posts the CustomerReturn ledger movement — EXT-CUSTOMER's External bucket
/// gives up every returned unit and the store's ReturnPending bucket receives
/// them — and writes the audit entry, all inside the unit-of-work transaction.
/// </summary>
/// <remarks>
/// <para>
/// Idempotency follows the completion flow: the device-generated
/// <see cref="CreateSalesReturnCommand.EventId"/> keys the ledger post, so a
/// retried return replays the stored outcome instead of double-posting.
/// </para>
/// <para>
/// The return window — commonly seven days for a full refund — is enforced by
/// the device only; the server never hard-codes a day count that would overrule
/// what a store configures. The server's insistence is structural: the sale
/// exists, is completed, is not voided and still holds returnable units.
/// </para>
/// </remarks>
public sealed class CreateSalesReturnCommandHandler(
    ISalesRepository repository,
    IShiftRepository shifts,
    IInventoryLedger ledger,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<CreateSalesReturnCommand, SalesReturnId>
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public async Task<Result<SalesReturnId>> HandleAsync(
        CreateSalesReturnCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ------------------------------------------------------------------
        // 1. Load the sale and verify the request matches its facts.
        // ------------------------------------------------------------------
        Sale? sale = await repository
            .GetByIdAsync(command.SaleId, cancellationToken)
            .ConfigureAwait(false);

        if (sale is null)
        {
            return Result<SalesReturnId>.Failure(ReturnCommandErrors.SaleNotFound(command.SaleId));
        }

        if (command.LocationId != sale.LocationId)
        {
            return Result<SalesReturnId>.Failure(
                ReturnCommandErrors.LocationMismatch(SalesReturnId.Empty, command.LocationId));
        }

        if (command.DeviceId != sale.DeviceId)
        {
            return Result<SalesReturnId>.Failure(
                ReturnCommandErrors.DeviceMismatch(SalesReturnId.Empty, command.DeviceId));
        }

        // ------------------------------------------------------------------
        // 2. Verify the device-allocated RET number's device code matches,
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
        // 3. Perform FIFO allocation of each requested product across the
        //    sale's lines in line-number order, respecting what each line
        //    still holds unreturned.
        // ------------------------------------------------------------------
        Dictionary<SaleItemId, decimal> alreadyReturned = sale.Items
            .ToDictionary(i => i.Id, i => i.ReturnedQuantity);

        List<ReturnItemSpec> specs = [];
        Dictionary<SaleItemId, decimal> allocated = sale.Items.ToDictionary(i => i.Id, _ => 0m);

        foreach (SalesReturnLine line in command.Lines)
        {
            decimal remaining = line.Quantity;
            bool foundAny = false;

            foreach (SaleItem item in sale.Items
                         .Where(i => i.ProductId == line.ProductId)
                         .OrderBy(i => i.LineNumber))
            {
                decimal cap = decimal.Round(
                    item.Quantity - alreadyReturned[item.Id],
                    Pos.Domain.Common.Quantity.Scale,
                    MidpointRounding.ToEven);

                decimal availableOnItem = decimal.Round(
                    cap - allocated[item.Id],
                    Pos.Domain.Common.Quantity.Scale,
                    MidpointRounding.ToEven);

                if (availableOnItem <= 0m)
                {
                    continue;
                }

                foundAny = true;
                decimal slice = remaining < availableOnItem ? remaining : availableOnItem;

                specs.Add(new ReturnItemSpec(item, slice));
                allocated[item.Id] = decimal.Round(
                    allocated[item.Id] + slice,
                    Pos.Domain.Common.Quantity.Scale,
                    MidpointRounding.ToEven);
                remaining = decimal.Round(
                    remaining - slice,
                    Pos.Domain.Common.Quantity.Scale,
                    MidpointRounding.ToEven);

                if (remaining <= 0m)
                {
                    break;
                }
            }

            if (!foundAny)
            {
                return Result<SalesReturnId>.Failure(ReturnCommandErrors.NoReturnableLines(line.ProductId));
            }

            if (remaining > 0m)
            {
                decimal requested = line.Quantity;
                decimal available = decimal.Round(requested - remaining, Pos.Domain.Common.Quantity.Scale, MidpointRounding.ToEven);
                return Result<SalesReturnId>.Failure(
                    ReturnCommandErrors.QuantityExceedsAvailable(line.ProductId, available, requested));
            }
        }

        // ------------------------------------------------------------------
        // 4. Create the return. The aggregate validates every line cap again
        //    against the same pre-return snapshot.
        // ------------------------------------------------------------------
        Result<SalesReturn> created = SalesReturn.Create(
            command.Number,
            command.EventId,
            command.SaleId,
            command.LocationId,
            command.ShiftId,
            command.DeviceId,
            command.CustomerId,
            command.BusinessDate,
            command.ReturnedAtUtc,
            command.ReturnedByUserId,
            specs,
            alreadyReturned);

        if (created.IsFailure)
        {
            return Result<SalesReturnId>.Failure(created.Errors);
        }

        SalesReturn salesReturn = created.Value;

        // ------------------------------------------------------------------
        // 5. Accumulate the returned quantities on each sale line, enforcing
        //    the server-side cap the sale itself holds.
        // ------------------------------------------------------------------
        foreach (SalesReturnItem returnItem in salesReturn.Items)
        {
            Result recorded = sale.RecordReturn(returnItem.SaleItemId, returnItem.Quantity);

            if (recorded.IsFailure)
            {
                return Result<SalesReturnId>.Failure(recorded.Errors);
            }
        }

        // ------------------------------------------------------------------
        // 6. Post the CustomerReturn ledger movement: EXT-CUSTOMER's External
        //    bucket gives up every returned unit and the store's ReturnPending
        //    bucket receives them, mirroring the original PosSale legs.
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
            bool tracksBatches = item.BatchId is not null;

            // Positive leg: store's ReturnPending bucket receives the goods.
            legs.Add(new MovementLegSpec(
                item.ProductId,
                item.BatchId,
                sale.LocationId,
                LocationKind.Store,
                InventoryState.ReturnPending,
                +item.Quantity,
                item.UnitCost,
                tracksBatches));

            // Negative leg: EXT-CUSTOMER's External bucket gives them up.
            legs.Add(new MovementLegSpec(
                item.ProductId,
                item.BatchId,
                externalCustomerLocationId.Value,
                LocationKind.External,
                InventoryState.External,
                -item.Quantity,
                item.UnitCost,
                tracksBatches));
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
        // 7. Audit the return.
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

        // ------------------------------------------------------------------
        // 8. Persist the new return and the updated sale.
        // ------------------------------------------------------------------
        Result<SalesReturnId> added = await repository
            .AddReturnAsync(salesReturn, cancellationToken)
            .ConfigureAwait(false);

        if (added.IsFailure)
        {
            return added;
        }

        Result<SaleId> updated = await repository
            .UpdateAsync(sale, cancellationToken)
            .ConfigureAwait(false);

        if (updated.IsFailure)
        {
            return Result<SalesReturnId>.Failure(updated.Errors);
        }

        return added;
    }
}