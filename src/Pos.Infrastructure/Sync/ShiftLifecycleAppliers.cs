using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// What a device tells the server about a shift. One shape for the whole
/// lifecycle: the fields a close carries are absent from an open, so the
/// appliers that need them say so rather than the device sending four payloads
/// that differ by three columns.
/// </summary>
/// <param name="ShiftId">The identity the device minted and printed.</param>
/// <param name="Number">The device-scoped SHF number.</param>
/// <param name="LocationId">Where the shift was opened.</param>
/// <param name="DeviceId">The register.</param>
/// <param name="CashierUserId">The cashier.</param>
/// <param name="OpeningFloat">The float counted into the drawer.</param>
/// <param name="BusinessDate">The business date in the location's timezone.</param>
/// <param name="OpenedAtUtc">The device clock when the drawer opened.</param>
/// <param name="Status">The shift's status after the event.</param>
/// <param name="ClosedAtUtc">When the drawer was closed, for a close.</param>
/// <param name="DeclaredCash">What the cashier said was in the drawer.</param>
/// <param name="CountedCash">What was actually counted.</param>
/// <param name="CashVariance">The variance the device printed on its Z-report.</param>
/// <param name="IsForceClosed">Whether a worker closed it rather than a cashier.</param>
internal sealed record ShiftUpload(
    Guid ShiftId,
    string Number,
    Guid LocationId,
    Guid DeviceId,
    Guid CashierUserId,
    decimal OpeningFloat,
    DateOnly BusinessDate,
    DateTimeOffset OpenedAtUtc,
    string Status,
    DateTimeOffset? ClosedAtUtc = null,
    decimal? DeclaredCash = null,
    decimal? CountedCash = null,
    decimal? CashVariance = null,
    bool IsForceClosed = false);

/// <summary>
/// The reading every shift applier does before it can act: parse the payload,
/// and refuse a device speaking for another device.
/// </summary>
internal static class ShiftUploads
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads the payload, or says why it cannot be acted on.</summary>
    /// <param name="payloadJson">The canonical payload.</param>
    /// <param name="deviceId">The device that uploaded it.</param>
    /// <returns>The upload, or the refusal to return instead.</returns>
    internal static (ShiftUpload? Upload, SyncApplyResult? Refusal) Read(string payloadJson, DeviceId deviceId)
    {
        ShiftUpload? upload;

        try
        {
            upload = JsonSerializer.Deserialize<ShiftUpload>(payloadJson, JsonDefaults);
        }
        catch (JsonException)
        {
            upload = null;
        }

        if (upload is null)
        {
            return (null, SyncApplyResult.Rejected("sync.payload_invalid", "The shift payload could not be read."));
        }

        // A device may only upload its own work, whatever the payload claims.
        if (upload.DeviceId != deviceId.Value)
        {
            return (null, SyncApplyResult.Rejected(
                "sync.device_mismatch",
                "The event names a different device than the one that uploaded it."));
        }

        return (upload, null);
    }

    /// <summary>
    /// Loads the shift an update event names, refusing one the server has never
    /// heard of or one that belongs to another register.
    /// </summary>
    /// <param name="shifts">The shift store.</param>
    /// <param name="deviceId">The uploading device.</param>
    /// <param name="upload">The parsed payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shift, or the refusal to return instead.</returns>
    internal static async Task<(CashierShift? Shift, SyncApplyResult? Refusal)> LoadAsync(
        IShiftRepository shifts,
        DeviceId deviceId,
        ShiftUpload upload,
        CancellationToken cancellationToken)
    {
        CashierShift? shift = await shifts
            .GetShiftAsync(new CashierShiftId(upload.ShiftId), cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            // The open event is still in flight or was refused. The processor's
            // ordering makes the first impossible, so this is the second: the
            // shift will never exist and retrying will never help.
            return (null, SyncApplyResult.Rejected(
                "sync.shift_unknown",
                "The shift this event updates is not recorded centrally."));
        }

        if (shift.DeviceId != deviceId)
        {
            return (null, SyncApplyResult.Rejected(
                "sync.device_mismatch",
                "The shift belongs to a different device."));
        }

        return (shift, null);
    }
}

/// <summary>
/// Applies a shift a device suspended offline — the cashier locked the till.
/// </summary>
/// <remarks>
/// The transition is replayed through the aggregate rather than written as a
/// status column, so a suspend arriving against a shift the server already
/// closed is refused by the same rule that would have refused it online.
/// </remarks>
/// <param name="shifts">The shift store.</param>
/// <param name="audit">The audit trail.</param>
public sealed class ShiftSuspendedApplier(IShiftRepository shifts, IAuditWriter audit) : ISyncEventApplier
{
    /// <inheritdoc />
    public string EventType => "ShiftSuspended";

    /// <inheritdoc />
    public async Task<SyncApplyResult> ApplyAsync(
        DeviceId deviceId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        (ShiftUpload? upload, SyncApplyResult? refusal) = ShiftUploads.Read(payloadJson, deviceId);

        if (upload is null)
        {
            return refusal!;
        }

        (CashierShift? shift, refusal) = await ShiftUploads
            .LoadAsync(shifts, deviceId, upload, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return refusal!;
        }

        Result suspended = shift.Suspend();

        if (suspended.IsFailure)
        {
            return SyncApplyResult.Rejected(suspended.Error.Code, suspended.Error.Message);
        }

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ShiftSuspended,
            "cashier_shift",
            shift.Id.Value,
            LocationId: shift.LocationId),
            cancellationToken).ConfigureAwait(false);

        await shifts.UpdateAsync(shift, cancellationToken).ConfigureAwait(false);

        return SyncApplyResult.Accepted(shift.Number);
    }
}

/// <summary>
/// Applies a shift a device resumed offline — the cashier unlocked the till.
/// </summary>
/// <param name="shifts">The shift store.</param>
/// <param name="audit">The audit trail.</param>
public sealed class ShiftResumedApplier(IShiftRepository shifts, IAuditWriter audit) : ISyncEventApplier
{
    /// <inheritdoc />
    public string EventType => "ShiftResumed";

    /// <inheritdoc />
    public async Task<SyncApplyResult> ApplyAsync(
        DeviceId deviceId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        (ShiftUpload? upload, SyncApplyResult? refusal) = ShiftUploads.Read(payloadJson, deviceId);

        if (upload is null)
        {
            return refusal!;
        }

        (CashierShift? shift, refusal) = await ShiftUploads
            .LoadAsync(shifts, deviceId, upload, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return refusal!;
        }

        Result resumed = shift.Resume();

        if (resumed.IsFailure)
        {
            return SyncApplyResult.Rejected(resumed.Error.Code, resumed.Error.Message);
        }

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ShiftResumed,
            "cashier_shift",
            shift.Id.Value,
            LocationId: shift.LocationId),
            cancellationToken).ConfigureAwait(false);

        await shifts.UpdateAsync(shift, cancellationToken).ConfigureAwait(false);

        return SyncApplyResult.Accepted(shift.Number);
    }
}

/// <summary>
/// Applies a drawer a device closed offline.
/// </summary>
/// <remarks>
/// <para>
/// The declared and counted cash are facts about a physical drawer, so they are
/// taken as the cashier entered them. The variance is not: it is derived, and
/// the server derives it again from the sales and refunds it actually accepted.
/// That is safe here because a device's events are applied in the order it
/// produced them, so every sale of the shift has already landed by the time its
/// close arrives — and it matters because the variance is what a manager
/// reconciles and what the cash report totals. A figure derived from events the
/// server refused would balance the books against sales it does not hold.
/// </para>
/// <para>
/// When the two disagree the audit entry records both, so the number on the
/// cashier's Z-report can still be explained rather than merely contradicted.
/// A close is never refused for the disagreement — the money has already moved,
/// and the reconcile step is where a human decides what it means.
/// </para>
/// </remarks>
/// <param name="shifts">The shift store.</param>
/// <param name="audit">The audit trail.</param>
public sealed class ShiftClosedApplier(IShiftRepository shifts, IAuditWriter audit) : ISyncEventApplier
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public string EventType => "ShiftClosed";

    /// <inheritdoc />
    public async Task<SyncApplyResult> ApplyAsync(
        DeviceId deviceId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        (ShiftUpload? upload, SyncApplyResult? refusal) = ShiftUploads.Read(payloadJson, deviceId);

        if (upload is null)
        {
            return refusal!;
        }

        // Force-close is the server's own worker closing a drawer nobody came
        // back to. A device claiming it would be claiming an authority it does
        // not have, and the claim is what would suppress the variance.
        if (upload.IsForceClosed)
        {
            return SyncApplyResult.Rejected(
                "sync.force_close_not_permitted",
                "A device may not force-close its own shift.");
        }

        if (upload.ClosedAtUtc is not { } closedAtUtc
            || upload.DeclaredCash is not { } declaredCash
            || upload.CountedCash is not { } countedCash)
        {
            return SyncApplyResult.Rejected(
                "sync.payload_invalid",
                "A shift closure must carry the closing instant and the declared and counted cash.");
        }

        (CashierShift? shift, refusal) = await ShiftUploads
            .LoadAsync(shifts, deviceId, upload, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return refusal!;
        }

        Result declared = shift.DeclareCash(declaredCash);

        if (declared.IsFailure)
        {
            return SyncApplyResult.Rejected(declared.Error.Code, declared.Error.Message);
        }

        ShiftCashTotals totals = await shifts
            .GetShiftCashTotalsAsync(shift.Id, cancellationToken)
            .ConfigureAwait(false);

        Result closed = shift.Close(
            countedCash,
            totals.CashSales,
            totals.CashRefunds,
            totals.Payouts,
            closedAtUtc);

        if (closed.IsFailure)
        {
            return SyncApplyResult.Rejected(closed.Error.Code, closed.Error.Message);
        }

        string auditJson = JsonSerializer.Serialize(new
        {
            shift.Id,
            shift.Number,
            shift.DeclaredCash,
            shift.CountedCash,
            shift.CashVariance,
            shift.ClosedAtUtc,
            DeviceReportedVariance = upload.CashVariance,
            totals.CashSales,
            totals.CashRefunds,
        }, JsonDefaults);

        string? reason = upload.CashVariance is { } reported && reported != shift.CashVariance
            ? FormattableString.Invariant(
                $"The device reported a variance of {reported}; the server derived {shift.CashVariance} from the sales and refunds it holds.")
            : null;

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ShiftClosed,
            "cashier_shift",
            shift.Id.Value,
            NewValueJson: auditJson,
            Reason: reason,
            LocationId: shift.LocationId),
            cancellationToken).ConfigureAwait(false);

        await shifts.UpdateAsync(shift, cancellationToken).ConfigureAwait(false);

        return SyncApplyResult.Accepted(shift.Number);
    }
}
