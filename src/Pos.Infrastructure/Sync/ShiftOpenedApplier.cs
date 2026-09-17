using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Applies a shift a device opened offline.
/// </summary>
/// <remarks>
/// <para>
/// The device is the authority for the shift's number and its opening float:
/// both were decided at the till, and the number is already printed on every
/// receipt the shift produced. The server's job is to accept the record, not to
/// re-derive it — re-numbering here would orphan those receipts.
/// </para>
/// <para>
/// What the server does re-check is whether the device may still do this. A
/// device revoked since the shift opened has its events refused, which is the
/// point of evaluating authorization now rather than as of event creation
/// (OFFLINE_SYNC.md §3.1 step 3).
/// </para>
/// </remarks>
/// <param name="context">The server database.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class ShiftOpenedApplier(PosDbContext context, ISystemClock clock) : ISyncEventApplier
{
    /// <inheritdoc />
    public string EventType => "ShiftOpened";

    /// <inheritdoc />
    public async Task<SyncApplyResult> ApplyAsync(
        DeviceId deviceId,
        EventId eventId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        (ShiftUpload? upload, SyncApplyResult? refusal) = ShiftUploads.Read(payloadJson, deviceId);

        if (upload is null)
        {
            return refusal!;
        }

        CashierShiftId shiftId = new(upload.ShiftId);

        bool alreadyHeld = await context.CashierShifts
            .AsNoTracking()
            .AnyAsync(s => s.Id == shiftId, cancellationToken)
            .ConfigureAwait(false);

        if (alreadyHeld)
        {
            // The identity is the device's, so the same shift arriving under a
            // second event identifier is a device fault, not a fresh shift.
            return SyncApplyResult.Rejected(
                "sync.shift_already_held",
                "This shift is already recorded centrally.");
        }

        Result<DocumentNumber> number = DocumentNumber.Parse(upload.Number);

        if (number.IsFailure)
        {
            return SyncApplyResult.Rejected("sync.payload_invalid", "The shift number is not a valid document number.");
        }

        Result<CashierShift> opened = CashierShift.Open(
            number.Value,
            new LocationId(upload.LocationId),
            deviceId,
            new UserId(upload.CashierUserId),
            upload.OpeningFloat,
            upload.BusinessDate,
            upload.OpenedAtUtc);

        if (opened.IsFailure)
        {
            return SyncApplyResult.Rejected(opened.Error.Code, opened.Error.Message);
        }

        CashierShift shift = opened.Value;
        context.CashierShifts.Add(shift);

        // The device's identifier is kept, not replaced. Sales uploaded from the
        // same device reference this shift by that identifier, so a server-minted
        // one would orphan them. The domain has no setter for identity — it is
        // not a business operation — so it is set here, where replaying a
        // device's record is the job.
        context.Entry(shift).Property(s => s.Id).CurrentValue = shiftId;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _ = clock;
        return SyncApplyResult.Accepted(upload.Number);
    }
}
