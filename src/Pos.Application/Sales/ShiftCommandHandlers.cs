using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
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
    /// <summary>How far ahead of head office a register's clock may run before
    /// a shift time it reports is refused.</summary>
    internal static readonly TimeSpan RegisterClockTolerance = TimeSpan.FromMinutes(5);

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
        // 4. Create and persist the shift, opened when the register says it
        //    was (an offline register uploads later), never in the future.
        // ------------------------------------------------------------------
        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset openedAt = command.OpenedAtUtc ?? now;
        if (openedAt > now + RegisterClockTolerance)
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.TimeInFuture);
        }

        Result<CashierShift> opened = CashierShift.Open(
            command.Number,
            command.LocationId,
            deviceId,
            cashierId,
            command.OpeningFloat,
            command.BusinessDate,
            openedAt,
            command.ShiftId);

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

        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset closedAt = command.ClosedAtUtc ?? now;
        if (closedAt > now + OpenShiftCommandHandler.RegisterClockTolerance)
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.TimeInFuture);
        }

        if (closedAt < shift.OpenedAtUtc)
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.ClosedBeforeOpened);
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
            closedAt);

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

/// <summary>
/// Handles <see cref="SuspendShiftCommand"/>. Suspends an open shift; the
/// shift is retained for resumption while the device is locked (POS.md §1).
/// Suspending another cashier's shift requires the <c>shift.close.other</c>
/// permission, verified here because the message can only declare one static
/// permission.
/// </summary>
public sealed class SuspendShiftCommandHandler(
    IShiftRepository shifts,
    IPermissionEvaluator permissions,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<SuspendShiftCommand, CashierShiftId>
{
    /// <inheritdoc />
    public async Task<Result<CashierShiftId>> HandleAsync(
        SuspendShiftCommand command,
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

        if (!await ShiftCommandAccess.CanOperate(shift, currentUser, permissions, cancellationToken))
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.CloseOtherShiftDenied);
        }

        Result suspended = shift.Suspend();

        if (suspended.IsFailure)
        {
            return Result<CashierShiftId>.Failure(suspended.Errors);
        }

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ShiftSuspended,
            "cashier_shift",
            shift.Id.Value,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        return await shifts
            .UpdateAsync(shift, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Handles <see cref="ResumeShiftCommand"/>. Returns a suspended shift to
/// <see cref="ShiftStatus.Open"/> so it can keep accepting sales (POS.md §1).
/// The ownership rule mirrors <see cref="SuspendShiftCommandHandler"/>.
/// </summary>
public sealed class ResumeShiftCommandHandler(
    IShiftRepository shifts,
    IPermissionEvaluator permissions,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<ResumeShiftCommand, CashierShiftId>
{
    /// <inheritdoc />
    public async Task<Result<CashierShiftId>> HandleAsync(
        ResumeShiftCommand command,
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

        if (!await ShiftCommandAccess.CanOperate(shift, currentUser, permissions, cancellationToken))
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.CloseOtherShiftDenied);
        }

        Result resumed = shift.Resume();

        if (resumed.IsFailure)
        {
            return Result<CashierShiftId>.Failure(resumed.Errors);
        }

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ShiftResumed,
            "cashier_shift",
            shift.Id.Value,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        return await shifts
            .UpdateAsync(shift, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Handles <see cref="ReconcileShiftCommand"/>. Marks a closed shift reconciled
/// (POS.md §1): the shift's variance is checked against the location's
/// <c>CashVarianceThreshold</c>, and a manager's reason is recorded when the
/// variance (or a force-close) requires one. Reconciling another cashier's
/// shift requires the <c>shift.close.other</c> permission, verified here.
/// </summary>
public sealed class ReconcileShiftCommandHandler(
    IShiftRepository shifts,
    IPermissionEvaluator permissions,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<ReconcileShiftCommand, CashierShiftId>
{
    /// <inheritdoc />
    public async Task<Result<CashierShiftId>> HandleAsync(
        ReconcileShiftCommand command,
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

        if (!await ShiftCommandAccess.CanOperate(shift, currentUser, permissions, cancellationToken))
        {
            return Result<CashierShiftId>.Failure(ShiftCommandErrors.CloseOtherShiftDenied);
        }

        // A shift whose location is missing fails closed to the strictest
        // configuration: zero tolerance, so a flagged variance still requires a
        // reason rather than passing silently.
        ShiftLocationFacts? location = await shifts
            .GetLocationFactsAsync(shift.LocationId, cancellationToken)
            .ConfigureAwait(false);

        decimal varianceThreshold = location?.Settings.CashVarianceThreshold ?? LocationSettings.Default.CashVarianceThreshold;

        Result reconciled = shift.Reconcile(varianceThreshold, command.Reason);

        if (reconciled.IsFailure)
        {
            return Result<CashierShiftId>.Failure(reconciled.Errors);
        }

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ShiftReconciled,
            "cashier_shift",
            shift.Id.Value,
            Reason: command.Reason,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        return await shifts
            .UpdateAsync(shift, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Shared ownership rule for operating a shift after it has been opened: the
/// shift's own cashier, or anyone holding <c>shift.close.other</c> at the
/// shift's location.
/// </summary>
internal static class ShiftCommandAccess
{
    /// <summary>Whether the current user may operate the shift.</summary>
    public static async Task<bool> CanOperate(
        CashierShift shift,
        ICurrentUser currentUser,
        IPermissionEvaluator permissions,
        CancellationToken cancellationToken)
    {
        UserId actor = currentUser.UserId ?? UserId.Empty;

        if (shift.CashierUserId == actor)
        {
            return true;
        }

        return await permissions
            .HasPermissionAsync(actor, Permissions.Sales.CloseOtherShift, shift.LocationId, cancellationToken)
            .ConfigureAwait(false);
    }
}
