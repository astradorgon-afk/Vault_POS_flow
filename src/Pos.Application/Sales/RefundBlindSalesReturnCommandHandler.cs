using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Handles <see cref="RefundBlindSalesReturnCommand"/>. The handler pre-checks
/// idempotency first — a replayed refund event returns the stored refund
/// without touching any other fact — then verifies the return exists, is a
/// blind return and belongs to the same location and device, and that the
/// shift is open on the same device. Because the return accepted goods back
/// with no original sale there is no sale to load and no per-method cap: the
/// handler hands the aggregate the location's cash rounding increment and lets
/// <see cref="SalesReturn.IssueBlindRefund"/> enforce the per-return cap. The
/// audit entry is written and the refund persisted inside the unit-of-work
/// transaction.
/// </summary>
public sealed class RefundBlindSalesReturnCommandHandler(
    ISalesRepository repository,
    IShiftRepository shifts,
    IAuditWriter audit) : ICommandHandler<RefundBlindSalesReturnCommand, RefundId>
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public async Task<Result<RefundId>> HandleAsync(
        RefundBlindSalesReturnCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ------------------------------------------------------------------
        // 1. Idempotency: a replayed refund event returns the stored refund.
        // ------------------------------------------------------------------
        Refund? existing = await repository
            .GetRefundByEventAsync(command.EventId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Result<RefundId>.Success(existing.Id);
        }

        // ------------------------------------------------------------------
        // 2. Load the return and verify the request matches its facts.
        // ------------------------------------------------------------------
        SalesReturn? salesReturn = await repository
            .GetReturnByIdAsync(command.SalesReturnId, cancellationToken)
            .ConfigureAwait(false);

        if (salesReturn is null)
        {
            return Result<RefundId>.Failure(RefundCommandErrors.ReturnNotFound(command.SalesReturnId));
        }

        if (!salesReturn.IsBlind)
        {
            return Result<RefundId>.Failure(RefundCommandErrors.ReturnNotBlind(salesReturn.Id));
        }

        if (command.LocationId != salesReturn.LocationId)
        {
            return Result<RefundId>.Failure(RefundCommandErrors.LocationMismatch(salesReturn.Id, command.LocationId));
        }

        if (command.DeviceId != salesReturn.DeviceId)
        {
            return Result<RefundId>.Failure(RefundCommandErrors.DeviceMismatch(salesReturn.Id, command.DeviceId));
        }

        // ------------------------------------------------------------------
        // 3. Verify the shift is open on the same device.
        // ------------------------------------------------------------------
        CashierShift? shift = await shifts
            .GetShiftAsync(command.ShiftId, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return Result<RefundId>.Failure(RefundCommandErrors.ShiftUnknown(command.ShiftId));
        }

        if (shift.Status != ShiftStatus.Open)
        {
            return Result<RefundId>.Failure(RefundCommandErrors.ShiftNotOpen(shift.Status));
        }

        if (shift.DeviceId != command.DeviceId)
        {
            return Result<RefundId>.Failure(RefundCommandErrors.ShiftDeviceMismatch);
        }

        // ------------------------------------------------------------------
        // 4. Load the location's cash rounding increment.
        // ------------------------------------------------------------------
        SaleLocationFacts? location = await repository
            .GetLocationAsync(salesReturn.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return Result<RefundId>.Failure(SaleCommandErrors.LocationUnknown(salesReturn.LocationId));
        }

        // ------------------------------------------------------------------
        // 5. Let the aggregate enforce the per-return cap. A blind return has
        //    no original sale, so no per-method cap applies.
        // ------------------------------------------------------------------
        Result<Refund> issued = salesReturn.IssueBlindRefund(
            command.EventId,
            command.ShiftId,
            command.DeviceId,
            command.Method,
            command.Amount,
            command.Tendered,
            command.ProviderReference,
            command.RefundedAtUtc,
            command.RefundedByUserId,
            location.Settings.CashRoundingIncrement);

        if (issued.IsFailure)
        {
            return Result<RefundId>.Failure(issued.Errors);
        }

        Refund refund = issued.Value;

        // ------------------------------------------------------------------
        // 6. Audit the refund.
        // ------------------------------------------------------------------
        string auditJson = JsonSerializer.Serialize(new
        {
            ReturnId = salesReturn.Id.Value,
            RefundId = refund.Id.Value,
            Method = refund.Method.ToString(),
            refund.Amount,
            refund.Tendered,
            ProviderReference = refund.ProviderReference,
            RefundedByUserId = refund.RefundedByUserId.Value,
        }, JsonDefaults);

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.RefundIssued,
            "sales_return",
            salesReturn.Id.Value,
            NewValueJson: auditJson,
            ReferenceDocumentType: ReferenceDocumentType.SalesReturn,
            ReferenceDocumentId: salesReturn.Id.Value,
            LocationId: salesReturn.LocationId),
            cancellationToken).ConfigureAwait(false);

        // ------------------------------------------------------------------
        // 7. Persist the refund.
        // ------------------------------------------------------------------
        return await repository
            .AddRefundAsync(salesReturn.Id, refund, cancellationToken)
            .ConfigureAwait(false);
    }
}