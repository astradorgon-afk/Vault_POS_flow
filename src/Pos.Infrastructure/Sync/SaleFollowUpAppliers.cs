using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Messaging;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Finding the sale a follow-up event is about.
/// </summary>
/// <remarks>
/// Always by SAL number, never by identifier. The server minted its own
/// <c>SaleId</c> when it replayed the sale, so the one a device names is
/// meaningless here; the number is the same on both sides and is what the
/// customer is holding.
/// </remarks>
internal static class SyncedSales
{
    internal static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads a follow-up payload, refusing one that speaks for another device.</summary>
    /// <typeparam name="TPayload">The payload shape.</typeparam>
    /// <param name="payloadJson">The canonical payload.</param>
    /// <param name="deviceId">The uploading device.</param>
    /// <param name="claimedDevice">Reads the device the payload names.</param>
    /// <returns>The payload, or the refusal to return instead.</returns>
    internal static (TPayload? Payload, SyncApplyResult? Refusal) Read<TPayload>(
        string payloadJson,
        DeviceId deviceId,
        Func<TPayload, Guid> claimedDevice)
        where TPayload : class
    {
        TPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<TPayload>(payloadJson, ReadOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null)
        {
            return (null, SyncApplyResult.Rejected("sync.payload_invalid", "The payload could not be read."));
        }

        if (claimedDevice(payload) != deviceId.Value)
        {
            return (null, SyncApplyResult.Rejected(
                "sync.device_mismatch",
                "The event names a different device than the one that uploaded it."));
        }

        return (payload, null);
    }

    /// <summary>Resolves the server's sale from the number printed at the till.</summary>
    /// <param name="context">The server database.</param>
    /// <param name="number">The SAL number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sale's server identifier, or null when the sale never arrived.</returns>
    internal static async Task<SaleId?> FindAsync(
        PosDbContext context,
        string number,
        CancellationToken cancellationToken)
    {
        SaleId found = await context.Sales
            .AsNoTracking()
            .Where(s => s.Number == number)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return found == SaleId.Empty ? null : found;
    }

    /// <summary>The refusal for a follow-up whose sale the server does not hold.</summary>
    /// <param name="number">The SAL number that was named.</param>
    /// <returns>The refusal.</returns>
    internal static SyncApplyResult SaleUnknown(string number)
        => SyncApplyResult.Rejected(
            "sync.sale_unknown",
            FormattableString.Invariant($"Sale {number} is not recorded centrally, so this event has nothing to act on."));
}

/// <summary>
/// Applies a sale a device voided offline.
/// </summary>
/// <remarks>
/// <para>
/// Replayed through <c>VoidSaleCommandHandler</c>, so the reversing ledger post
/// and the same-shift rule come from the one implementation rather than a second
/// one written for sync. The void's idempotency key is the sync event
/// identifier: it is stable across retries in a way a freshly minted one would
/// not be, so a batch that times out after the ledger posted does not reverse
/// the stock twice when it is sent again.
/// </para>
/// <para>
/// A void is never quietly dropped for want of its sale. Under per-device
/// ordering the sale was uploaded first, so a missing one means that upload was
/// refused — and a void floating free of the sale it reverses would take stock
/// back onto a shelf against nothing.
/// </para>
/// </remarks>
/// <param name="context">The server database.</param>
/// <param name="handler">The one void handler, shared with the online path.</param>
public sealed class SaleVoidedApplier(
    PosDbContext context,
    ICommandHandler<VoidSaleCommand, SaleId> handler) : ISyncEventApplier
{
    /// <inheritdoc />
    public string EventType => "SaleVoided";

    /// <inheritdoc />
    public async Task<SyncApplyResult> ApplyAsync(
        DeviceId deviceId,
        EventId eventId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        (SaleVoidSyncPayload? upload, SyncApplyResult? refusal) =
            SyncedSales.Read<SaleVoidSyncPayload>(payloadJson, deviceId, p => p.DeviceId);

        if (upload is null)
        {
            return refusal!;
        }

        if (upload.VoidedByUserId is not { } voidedBy
            || upload.VoidedAtUtc is not { } voidedAt
            || string.IsNullOrWhiteSpace(upload.Reason))
        {
            return SyncApplyResult.Rejected(
                "sync.payload_invalid",
                "A void must name who did it, when, and why.");
        }

        if (await SyncedSales.FindAsync(context, upload.Number, cancellationToken).ConfigureAwait(false)
            is not { } saleId)
        {
            return SyncedSales.SaleUnknown(upload.Number);
        }

        Result<SaleId> applied = await handler.HandleAsync(
            new VoidSaleCommand(
                eventId,
                saleId,
                new LocationId(upload.LocationId),
                new CashierShiftId(upload.ShiftId),
                deviceId,
                upload.BusinessDate,
                new UserId(voidedBy),
                voidedAt,
                upload.Reason),
            cancellationToken).ConfigureAwait(false);

        return applied.IsFailure
            ? SyncApplyResult.Rejected(applied.Error.Code, applied.Error.Message)
            : SyncApplyResult.Accepted(upload.Number);
    }
}

/// <summary>
/// Applies a receipt a device reprinted offline.
/// </summary>
/// <remarks>
/// A reprint changes no money and no stock, which is exactly why it has to
/// arrive: somebody can walk out of the shop with a second copy of a receipt and
/// bring it back as a return. The print log is the only thing that makes that
/// visible, and a log that silently skips the offline copies is worse than none,
/// because it is trusted.
/// </remarks>
/// <param name="context">The server database.</param>
/// <param name="handler">The one reprint handler, shared with the online path.</param>
public sealed class SaleReceiptReprintedApplier(
    PosDbContext context,
    ICommandHandler<ReprintSaleReceiptCommand, SaleId> handler) : ISyncEventApplier
{
    /// <inheritdoc />
    public string EventType => "SaleReceiptReprinted";

    /// <inheritdoc />
    public async Task<SyncApplyResult> ApplyAsync(
        DeviceId deviceId,
        EventId eventId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        (ReceiptPrintSyncPayload? upload, SyncApplyResult? refusal) =
            SyncedSales.Read<ReceiptPrintSyncPayload>(payloadJson, deviceId, p => p.DeviceId);

        if (upload is null)
        {
            return refusal!;
        }

        if (string.IsNullOrWhiteSpace(upload.Reason))
        {
            return SyncApplyResult.Rejected(
                "sync.payload_invalid",
                "A reprint must say why a second copy was needed.");
        }

        if (await SyncedSales.FindAsync(context, upload.Number, cancellationToken).ConfigureAwait(false)
            is not { } saleId)
        {
            return SyncedSales.SaleUnknown(upload.Number);
        }

        _ = eventId;

        Result<SaleId> applied = await handler.HandleAsync(
            new ReprintSaleReceiptCommand(
                saleId,
                new LocationId(upload.LocationId),
                deviceId,
                upload.Reason,
                upload.PrintedAtUtc,
                new UserId(upload.PrintedByUserId)),
            cancellationToken).ConfigureAwait(false);

        return applied.IsFailure
            ? SyncApplyResult.Rejected(applied.Error.Code, applied.Error.Message)
            : SyncApplyResult.Accepted(upload.Number);
    }
}
