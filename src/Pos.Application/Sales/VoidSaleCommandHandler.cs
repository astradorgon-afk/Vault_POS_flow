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
/// Handles <see cref="VoidSaleCommand"/>. The handler verifies the sale exists
/// and belongs to the request's location and device, that the shift it is being
/// voided against exists and is open, then lets the aggregate validate that it
/// is still completed and that the shift and business date match. It posts the
/// reversal of the original PosSale ledger movement — EXT-CUSTOMER's External
/// bucket back into the store's Available bucket — and writes the audit entry,
/// all inside the unit-of-work transaction.
/// </summary>
/// <remarks>
/// Idempotency follows the completion flow: the device-generated
/// <see cref="VoidSaleCommand.EventId"/> keys the ledger post, so a retried void
/// replays the stored outcome instead of double-posting. A retry that reaches
/// the domain sees the sale already voided and fails with
/// <see cref="SaleErrors.VoidOnlyCompleted"/>, which a client treats as
/// already-done.
/// </remarks>
public sealed class VoidSaleCommandHandler(
    ISalesRepository repository,
    IShiftRepository shifts,
    IInventoryLedger ledger,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<VoidSaleCommand, SaleId>
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public async Task<Result<SaleId>> HandleAsync(
        VoidSaleCommand command,
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
            return Result<SaleId>.Failure(VoidSaleCommandErrors.SaleNotFound(command.SaleId));
        }

        if (command.LocationId != sale.LocationId)
        {
            return Result<SaleId>.Failure(VoidSaleCommandErrors.LocationMismatch(sale.Id, command.LocationId));
        }

        if (command.DeviceId != sale.DeviceId)
        {
            return Result<SaleId>.Failure(VoidSaleCommandErrors.DeviceMismatch(sale.Id, command.DeviceId));
        }

        // ------------------------------------------------------------------
        // 2. Verify the shift the void happens in exists and is open.
        // ------------------------------------------------------------------
        CashierShift? shift = await shifts
            .GetShiftAsync(command.ShiftId, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.ShiftUnknown(command.ShiftId));
        }

        if (shift.Status != ShiftStatus.Open)
        {
            return Result<SaleId>.Failure(SaleCommandErrors.ShiftNotOpen(shift.Status));
        }

        // ------------------------------------------------------------------
        // 3. Flip the sale's own state. The aggregate validates that it is
        //    still completed and that the shift and business date belong to it.
        // ------------------------------------------------------------------
        Result voided = sale.Void(
            command.ShiftId,
            command.BusinessDate,
            command.VoidedAtUtc,
            command.VoidedByUserId,
            command.Reason);

        if (voided.IsFailure)
        {
            return Result<SaleId>.Failure(voided.Errors);
        }

        // ------------------------------------------------------------------
        // 4. Post the PosSaleVoid ledger movement: EXT-CUSTOMER's External
        //    bucket gives every sold unit back into the store's Available
        //    bucket, mirroring the original PosSale legs.
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
            // A sold line carries the batch when the product tracks batches, so
            // the line itself tells the ledger which bucket to restore.
            bool tracksBatches = item.BatchId is not null;

            // Positive leg: store's Available bucket gets the units back.
            legs.Add(new MovementLegSpec(
                item.ProductId,
                item.BatchId,
                sale.LocationId,
                LocationKind.Store,
                InventoryState.Available,
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
            MovementType: InventoryMovementType.PosSaleVoid,
            ReferenceDocumentType: ReferenceDocumentType.Sale,
            ReferenceDocumentId: sale.Id.Value,
            ReferenceNumber: sale.Number,
            Legs: legs,
            Actor: new LedgerActor(
                CreatedBy: command.VoidedByUserId,
                // PosSaleVoid requires an approving user; for a same-shift void
                // the acting user is the approver.
                ApprovedBy: command.VoidedByUserId,
                Device: currentUser.DeviceId,
                Correlation: currentUser.CorrelationId),
            OccurredAtUtc: command.VoidedAtUtc,
            BusinessDate: command.BusinessDate);

        Result<PostedMovementGroup> posted = await ledger
            .PostAsync(movementGroup, cancellationToken)
            .ConfigureAwait(false);

        if (posted.IsFailure)
        {
            return Result<SaleId>.Failure(posted.Errors);
        }

        // ------------------------------------------------------------------
        // 5. Audit the void.
        // ------------------------------------------------------------------
        string auditJson = JsonSerializer.Serialize(new
        {
            sale.Id,
            sale.Number,
            LocationId = command.LocationId.Value,
            sale.NetTotal,
            VoidedByUserId = command.VoidedByUserId.Value,
            Reason = command.Reason,
        }, JsonDefaults);

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.SaleVoided,
            "sale",
            sale.Id.Value,
            NewValueJson: auditJson,
            Reason: command.Reason,
            ReferenceDocumentType: ReferenceDocumentType.Sale,
            ReferenceDocumentId: sale.Id.Value,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        // ------------------------------------------------------------------
        // 6. Persist the changed sale.
        // ------------------------------------------------------------------
        return await repository
            .UpdateAsync(sale, cancellationToken)
            .ConfigureAwait(false);
    }
}