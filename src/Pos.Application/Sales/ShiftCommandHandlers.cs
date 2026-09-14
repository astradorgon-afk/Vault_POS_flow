using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Handles <see cref="OpenShiftCommand"/>. Opens a shift for the authenticated
/// device and cashier: verifies the device-scoped SHF number was issued by the
/// posting device, refuses a second open shift on the device, and persists the
/// new shift inside the unit-of-work transaction.
/// </summary>
public sealed class OpenShiftCommandHandler(
    IShiftRepository shifts,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<OpenShiftCommand, CashierShiftId>
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public async Task<Result<CashierShiftId>> HandleAsync(
        OpenShiftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.DeviceId is not { } deviceId)
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.DeviceRequired);
        }

        UserId cashierId = currentUser.UserId ?? UserId.Empty;

        // ------------------------------------------------------------------
        // 1. Verify the device and the SHF number it claims to have issued.
        // ------------------------------------------------------------------
        ShiftDeviceFacts? device = await shifts
            .GetDeviceFactsAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.DeviceUnknown(deviceId));
        }

        if (command.Number.DeviceShortCode is null
            || !string.Equals(command.Number.DeviceShortCode, device.ShortCode, StringComparison.OrdinalIgnoreCase))
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.NumberDeviceMismatch);
        }

        // ------------------------------------------------------------------
        // 2. Resolve the location.
        // ------------------------------------------------------------------
        ShiftLocationFacts? location = await shifts
            .GetLocationFactsAsync(command.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.LocationUnknown(command.LocationId));
        }

        if (location.Kind == LocationKind.External)
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.LocationExternal);
        }

        // ------------------------------------------------------------------
        // 3. Refuse a second open shift on the device.
        // ------------------------------------------------------------------
        CashierShift? open = await shifts
            .GetOpenShiftForDeviceAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (open is not null)
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.ShiftAlreadyOpen(deviceId));
        }

        // ------------------------------------------------------------------
        // 4. Create and persist the shift.
        // ------------------------------------------------------------------
        Result<CashierShift> opened = CashierShift.Open(
            command.Number,
            command.LocationId,
            deviceId,
            cashierId,
            command.OpeningFloat,
            command.BusinessDate,
            clock.UtcNow);

        if (opened.IsFailure)
        {
            return Result<CashierShiftId>.Failure(opened.Errors);
        }

        CashierShift shift = opened.Value;

        string auditJson = JsonSerializer.Serialize(new
        {
            shift.Id,
            shift.Number,
            LocationId = shift.LocationId.Value,
            DeviceId = shift.DeviceId.Value,
            CashierUserId = shift.CashierUserId.Value,
            shift.OpeningFloat,
            shift.BusinessDate,
        }, JsonDefaults);

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ShiftOpened,
            "cashier_shift",
            shift.Id.Value,
            NewValueJson: auditJson,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        return await shifts
            .AddAsync(shift, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Handles <see cref="CloseShiftCommand"/>. Declares the cash on hand, counts
/// the drawer, computes the variance against the expected float-plus-cash-sales
/// figure, and records the closure (POS.md §1). Closing another cashier's
/// shift requires the <c>shift.close.other</c> permission, which this handler
/// verifies because the message can only declare one static permission.
/// </summary>
public sealed class CloseShiftCommandHandler(
    IShiftRepository shifts,
    IPermissionEvaluator permissions,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CloseShiftCommand, CashierShiftId>
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public async Task<Result<CashierShiftId>> HandleAsync(
        CloseShiftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        CashierShift? shift = await shifts
            .GetShiftAsync(command.ShiftId, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return Result<CashierShiftId>.Failure(ShiftErrors.ShiftUnknown(command.ShiftId));
        }

        if (shift.LocationId != command.LocationId)
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.LocationMismatch);
        }

        UserId actor = currentUser.UserId ?? UserId.Empty;

        if (shift.CashierUserId != actor)
        {
            bool canCloseOther = await permissions
                .HasPermissionAsync(actor, Permissions.Sales.CloseOtherShift, shift.LocationId, cancellationToken)
                .ConfigureAwait(false);

            if (!canCloseOther)
            {
                return Result<CashierShiftId>.Failure(ShiftCommandErrors.CloseOtherShiftDenied);
            }
        }

        Result declared = shift.DeclareCash(command.DeclaredCash);

        if (declared.IsFailure)
        {
            return Result<CashierShiftId>.Failure(declared.Errors);
        }

        ShiftCashTotals totals = await shifts
            .GetShiftCashTotalsAsync(shift.Id, cancellationToken)
            .ConfigureAwait(false);

        Result closed = shift.Close(
            command.CountedCash,
            totals.CashSales,
            totals.CashRefunds,
            totals.Payouts,
            clock.UtcNow);

        if (closed.IsFailure)
        {
            return Result<CashierShiftId>.Failure(closed.Errors);
        }

        string auditJson = JsonSerializer.Serialize(new
        {
            shift.Id,
            shift.Number,
            shift.DeclaredCash,
            shift.CountedCash,
            shift.CashVariance,
            shift.ClosedAtUtc,
        }, JsonDefaults);

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ShiftClosed,
            "cashier_shift",
            shift.Id.Value,
            NewValueJson: auditJson,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        return await shifts
            .UpdateAsync(shift, cancellationToken)
            .ConfigureAwait(false);
    }
}