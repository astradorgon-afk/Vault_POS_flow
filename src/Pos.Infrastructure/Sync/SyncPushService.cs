using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Sales;
using Pos.Application.Transfers;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Transfers;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>Accepts device events without ever claiming an unimplemented event was applied.</summary>
public sealed class SyncPushService(
    PosDbContext context,
    ICurrentUser currentUser,
    ISystemClock clock,
    IDispatcher dispatcher,
    IUnitOfWork unitOfWork,
    ICurrentUserOverride replayOverride)
{
    private static readonly TimeSpan GapTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Processes one push batch in device-sequence order.</summary>
    public async Task<SyncPushResponse> PushAsync(
        SyncPushRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = clock.UtcNow;
        DeviceId? authenticatedDevice = currentUser.DeviceId;
        UserId? authenticatedUser = currentUser.UserId;

        if (authenticatedDevice is null || authenticatedUser is null || authenticatedDevice.Value.Value != request.DeviceId)
        {
            return SyncPushResponse.Refused("sync.device_binding", "The upload device does not match the authenticated device.");
        }

        Device? device = await context.Devices
            .SingleOrDefaultAsync(d => d.Id == authenticatedDevice.Value, cancellationToken)
            .ConfigureAwait(false);

        if (device is null || !device.IsOperational)
        {
            return SyncPushResponse.Refused("sync.device_not_operational", "The device is not permitted to synchronize.");
        }

        if (request.Events.Count > 100)
        {
            return SyncPushResponse.Refused("sync.batch_too_large", "A synchronization batch may contain at most 100 events.");
        }

        List<SyncPushEventResult> results = [];
        foreach (SyncPushEvent item in request.Events.OrderBy(e => e.DeviceSequence))
        {
            results.Add(await ProcessOneAsync(
                request.DeviceId,
                authenticatedUser.Value,
                device.LocationId,
                item,
                now,
                cancellationToken).ConfigureAwait(false));
        }

        device.RecordSync(now);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        long cursor = await context.SyncDeviceCheckpoints
            .Where(c => c.DeviceId == device.Id)
            .Select(c => (long?)c.LastAcceptedSequence)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return new SyncPushResponse(now, results, cursor, null, null);
    }

    /// <summary>Retries one stored event under its original device identity.</summary>
    public async Task<Result> RetryFailureAsync(Guid failureId, CancellationToken cancellationToken)
    {
        SyncFailure? failure = await context.SyncFailures
            .SingleOrDefaultAsync(f => f.Id == failureId, cancellationToken)
            .ConfigureAwait(false);
        if (failure is null)
        {
            return Result.Failure(Error.NotFound("sync.failure_unknown", "The synchronization failure was not found."));
        }

        if (failure.Status != SyncFailureStatus.RetryScheduled
            || failure.NextRetryAtUtc is not { } dueAt
            || dueAt > clock.UtcNow)
        {
            return Result.Failure(Error.Conflict("sync.failure_not_due", "The synchronization failure is not due for retry."));
        }

        ProcessedSyncEvent? stored = await context.ProcessedSyncEvents
            .SingleOrDefaultAsync(e => e.EventId == failure.EventId, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null || !IsReplayableEvent(stored.EventType))
        {
            return Result.Failure(Error.Unavailable("sync.handler_unavailable", "No retry handler is registered for this event."));
        }

        replayOverride.Set(stored.UserId, stored.DeviceId, stored.LocationId, stored.CorrelationId);
        SyncPushEvent item = new(
            stored.EventId.Value,
            stored.DeviceSequence,
            stored.EventType,
            stored.PayloadJson,
            Convert.ToBase64String(stored.PayloadHash),
            stored.UserId.Value,
            stored.LocationId.Value,
            stored.OccurredAtUtc,
            stored.DeviceUptimeTicks,
            stored.CorrelationId.Value);

        await using IUnitOfWorkTransaction transaction =
            await unitOfWork.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Result<Guid> replay = await ReplayBusinessEventAsync(item, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = clock.UtcNow;
        if (replay.IsSuccess)
        {
            stored.MarkAccepted(now, $"{{\"entityId\":\"{replay.Value}\"}}");
            failure.Resolve(now, "Automatically replayed successfully.");
        }
        else
        {
            failure.ScheduleRetry(now);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return replay.IsSuccess ? Result.Success() : Result.Failure(replay.Error);
    }

    private async Task<SyncPushEventResult> ProcessOneAsync(
        Guid deviceId,
        UserId authenticatedUser,
        LocationId deviceLocation,
        SyncPushEvent item,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (item.EventId == Guid.Empty || item.DeviceSequence <= 0 || item.PayloadJson.Length == 0)
        {
            return SyncPushEventResult.Rejected(item.EventId, "sync.event_invalid", "The event envelope is invalid.");
        }

        byte[] suppliedHash;
        try
        {
            suppliedHash = Convert.FromBase64String(item.PayloadHash);
        }
        catch (FormatException)
        {
            return SyncPushEventResult.Rejected(item.EventId, "sync.payload_hash_invalid", "The payload hash is not valid base64.");
        }

        byte[] computedHash = SHA256.HashData(Encoding.UTF8.GetBytes(item.PayloadJson));
        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, computedHash))
        {
            return SyncPushEventResult.Rejected(item.EventId, "sync.payload_hash_mismatch", "The payload hash does not match the payload.");
        }

        ProcessedSyncEvent? existing = await context.ProcessedSyncEvents
            .SingleOrDefaultAsync(e => e.EventId == new EventId(item.EventId), cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(existing.PayloadHash, suppliedHash))
            {
                return SyncPushEventResult.Rejected(item.EventId, "sync.idempotency_key_reuse", "The event identifier was reused with different content.");
            }

            if (existing.DeviceSequence != item.DeviceSequence
                || !string.Equals(existing.EventType, item.EventType, StringComparison.Ordinal))
            {
                bool failureExists = await context.SyncFailures
                    .AnyAsync(f => f.EventId == new EventId(item.EventId), cancellationToken)
                    .ConfigureAwait(false);
                if (!failureExists)
                {
                    context.SyncFailures.Add(new SyncFailure(
                        new EventId(item.EventId),
                        new DeviceId(deviceId),
                        "sync.event_metadata_mismatch",
                        "The event identifier was replayed with different device metadata.",
                        now));
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                return SyncPushEventResult.Conflict(
                    item.EventId,
                    "sync.event_metadata_mismatch",
                    "The event identifier was replayed with different device metadata.");
            }

            return SyncPushEventResult.Duplicate(item.EventId, existing.Outcome, existing.AppliedAtUtc);
        }

        if (item.UserId != authenticatedUser.Value || item.LocationId != deviceLocation.Value)
        {
            return SyncPushEventResult.Rejected(item.EventId, "sync.event_scope", "The event identity or location is outside the authenticated device scope.");
        }

        SyncDeviceCheckpoint? existingCheckpoint = await context.SyncDeviceCheckpoints
            .SingleOrDefaultAsync(c => c.DeviceId == new DeviceId(deviceId), cancellationToken)
            .ConfigureAwait(false);
        bool isNewCheckpoint = existingCheckpoint is null;
        SyncDeviceCheckpoint checkpoint = existingCheckpoint ?? new SyncDeviceCheckpoint(new DeviceId(deviceId), 0, now);

        ProcessedSyncEvent? sequenceOwner = await context.ProcessedSyncEvents
            .SingleOrDefaultAsync(
                e => e.DeviceId == new DeviceId(deviceId) && e.DeviceSequence == item.DeviceSequence,
                cancellationToken)
            .ConfigureAwait(false);

        if (sequenceOwner is not null && sequenceOwner.EventId.Value != item.EventId)
        {
            context.SyncFailures.Add(new SyncFailure(
                new EventId(item.EventId),
                new DeviceId(deviceId),
                "sync.sequence_reuse",
                "The device sequence was already used by a different event identifier.",
                now));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return SyncPushEventResult.Conflict(
                item.EventId,
                "sync.sequence_reuse",
                "The device sequence was already used by a different event identifier.");
        }

        if (item.DeviceSequence <= checkpoint.LastAcceptedSequence)
        {
            return SyncPushEventResult.Conflict(
                item.EventId,
                "sync.sequence_expired",
                "The event sequence arrived after an earlier gap was closed; rebaseline is required.");
        }

        if (item.DeviceSequence != checkpoint.LastAcceptedSequence + 1)
        {
            checkpoint.MarkGapDetected(now);
            if (checkpoint.GapHasExpired(now, GapTimeout))
            {
                ProcessedSyncEvent skipped = new(
                    new EventId(item.EventId),
                    new DeviceId(deviceId),
                    item.DeviceSequence,
                    item.EventType,
                    item.PayloadJson,
                    suppliedHash,
                    new UserId(item.UserId),
                    new LocationId(item.LocationId),
                    item.OccurredAtUtc,
                    item.DeviceUptimeTicks,
                    new CorrelationId(item.CorrelationId == Guid.Empty ? Guid.CreateVersion7() : item.CorrelationId),
                    SyncProcessingOutcome.RequiresReview,
                    now,
                    "{\"code\":\"sync.sequence_gap_timeout\"}");
                context.ProcessedSyncEvents.Add(skipped);
                context.SyncFailures.Add(new SyncFailure(
                    new EventId(item.EventId),
                    new DeviceId(deviceId),
                    "sync.sequence_gap_timeout",
                    "The missing device sequence did not arrive before the gap timeout.",
                    now));
                if (isNewCheckpoint)
                {
                    context.SyncDeviceCheckpoints.Add(checkpoint);
                }

                checkpoint.SkipToAfterExpiredGap(item.DeviceSequence, now);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return SyncPushEventResult.RequiresReview(
                    item.EventId,
                    now,
                    "sync.sequence_gap_timeout",
                    "The missing device sequence did not arrive before the gap timeout.");
            }

            if (isNewCheckpoint)
            {
                context.SyncDeviceCheckpoints.Add(checkpoint);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return SyncPushEventResult.Deferred(item.EventId, "sync.sequence_gap", "The event is waiting for an earlier device sequence.");
        }

        if (IsReplayableEvent(item.EventType))
        {
            await using IUnitOfWorkTransaction transaction =
                await unitOfWork.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            Result<Guid> replay = await ReplayBusinessEventAsync(item, cancellationToken).ConfigureAwait(false);
            if (replay.IsSuccess)
            {
                await RecordProcessedAsync(
                    item,
                    deviceId,
                    checkpoint,
                    isNewCheckpoint,
                    SyncProcessingOutcome.Accepted,
                    now,
                    responseJson: $"{{\"entityId\":\"{replay.Value}\"}}",
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SyncPushEventResult.Accepted(item.EventId, now);
            }

            string? remediation = RemediationForReplayFailure(replay.Error);
            bool hardConflict = replay.Error.Code == "transfer.invalid_state";
            SyncProcessingOutcome failedOutcome = hardConflict
                ? SyncProcessingOutcome.Conflict
                : RequiresReviewAfterReplayFailure(replay.Error)
                ? SyncProcessingOutcome.RequiresReview
                : SyncProcessingOutcome.Rejected;

            await RecordProcessedAsync(
                item,
                deviceId,
                checkpoint,
                isNewCheckpoint,
                failedOutcome,
                now,
                responseJson: JsonSerializer.Serialize(new { code = replay.Error.Code, remediation }),
                cancellationToken).ConfigureAwait(false);
            context.SyncFailures.Add(new SyncFailure(
                new EventId(item.EventId),
                new DeviceId(deviceId),
                replay.Error.Code,
                replay.Error.Message,
                now));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return hardConflict
                ? SyncPushEventResult.Conflict(item.EventId, replay.Error.Code, replay.Error.Message)
                : failedOutcome == SyncProcessingOutcome.RequiresReview
                ? SyncPushEventResult.RequiresReview(item.EventId, now, replay.Error.Code, replay.Error.Message)
                : SyncPushEventResult.Rejected(item.EventId, replay.Error.Code, replay.Error.Message, remediation);
        }

        // Event handlers that are not implemented yet are still durable. They
        // are parked rather than reported as accepted, so a future review can
        // replay them without pretending the business effect already happened.
        ProcessedSyncEvent parked = new(
            new EventId(item.EventId),
            new DeviceId(deviceId),
            item.DeviceSequence,
            item.EventType,
            item.PayloadJson,
            suppliedHash,
            new UserId(item.UserId),
            new LocationId(item.LocationId),
            item.OccurredAtUtc,
            item.DeviceUptimeTicks,
            new CorrelationId(item.CorrelationId == Guid.Empty ? Guid.CreateVersion7() : item.CorrelationId),
            SyncProcessingOutcome.RequiresReview,
            now,
            "{\"code\":\"sync.handler_unavailable\"}");

        context.ProcessedSyncEvents.Add(parked);
        context.SyncFailures.Add(new SyncFailure(
            new EventId(item.EventId),
            new DeviceId(deviceId),
            "sync.handler_unavailable",
            "The event was recorded for server-side review.",
            now));
        if (isNewCheckpoint)
        {
            context.SyncDeviceCheckpoints.Add(checkpoint);
        }

        checkpoint.Advance(item.DeviceSequence, now);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return SyncPushEventResult.Parked(item.EventId, now);
    }

    private async Task<Result<Guid>> ReplayBusinessEventAsync(
        SyncPushEvent item,
        CancellationToken cancellationToken)
    {
        if (IsShiftEvent(item.EventType))
        {
            Result<CashierShiftId> shift = await ReplayShiftAsync(item, cancellationToken).ConfigureAwait(false);
            return shift.IsSuccess
                ? Result<Guid>.Success(shift.Value.Value)
                : Result<Guid>.Failure(shift.Errors);
        }

        if (item.EventType == "SaleCompleted")
        {
            return await ReplaySaleAsync(item, cancellationToken).ConfigureAwait(false);
        }

        if (item.EventType == "TransferReceived")
        {
            return await ReplayTransferReceiptAsync(item, cancellationToken).ConfigureAwait(false);
        }

        return Result<Guid>.Failure(Error.Validation(
            "sync.event_type_unknown", "The event type is not supported by the replay handler."));
    }

    private static bool RequiresReviewAfterReplayFailure(Error error)
        // A valid offline event can become impossible because server state or
        // authority changed while the device was disconnected. Keep that event
        // visible for reconciliation instead of presenting it as malformed.
        => error.Type is ErrorType.Conflict or ErrorType.Forbidden;

    private static string? RemediationForReplayFailure(Error error)
        => error.Code is "sale.product_unknown" or "sale.customer_unknown"
            ? "QuarantineAndReview"
            : null;

    private async Task<Result<Guid>> ReplayTransferReceiptAsync(
        SyncPushEvent item,
        CancellationToken cancellationToken)
    {
        TransferReceiveSyncPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TransferReceiveSyncPayload>(item.PayloadJson);
        }
        catch (JsonException)
        {
            return Result<Guid>.Failure(Error.Validation(
                "sync.payload_invalid", "The transfer receipt payload is invalid."));
        }

        if (payload is null || payload.TransferId == Guid.Empty || payload.Receives.Count == 0)
        {
            return Result<Guid>.Failure(Error.Validation(
                "sync.payload_invalid", "The transfer receipt payload is incomplete."));
        }

        TransferReceiveAllocationSpec[] receives =
        [
            .. payload.Receives.Select(receive => new TransferReceiveAllocationSpec(
                receive.LineNo,
                receive.BatchId is { } batchId ? new BatchId(batchId) : null,
                receive.ReceivedQuantity,
                receive.DamagedQuantity)),
        ];

        Result<TransferOrderId> replay = await dispatcher.SendAsync(
            new ReceiveTransferCommand(new TransferOrderId(payload.TransferId), receives),
            cancellationToken).ConfigureAwait(false);

        return replay.IsSuccess
            ? Result<Guid>.Success(replay.Value.Value)
            : Result<Guid>.Failure(replay.Errors);
    }

    private async Task<Result<CashierShiftId>> ReplayShiftAsync(
        SyncPushEvent item,
        CancellationToken cancellationToken)
    {
        ShiftSyncPayload? payload;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<ShiftSyncPayload>(item.PayloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return Result<CashierShiftId>.Failure(Error.Validation(
                "sync.payload_invalid", "The shift event payload is invalid."));
        }

        if (payload is null || payload.ShiftId == Guid.Empty || payload.LocationId == Guid.Empty)
        {
            return Result<CashierShiftId>.Failure(Error.Validation(
                "sync.payload_invalid", "The shift event payload is incomplete."));
        }

        LocationId locationId = new(payload.LocationId);
        CashierShiftId shiftId = new(payload.ShiftId);

        return item.EventType switch
        {
            "ShiftOpened" => await ReplayOpenAsync(payload, locationId, shiftId, cancellationToken).ConfigureAwait(false),
            "ShiftSuspended" => await dispatcher.SendAsync(
                new SuspendShiftCommand(shiftId, locationId), cancellationToken).ConfigureAwait(false),
            "ShiftResumed" => await dispatcher.SendAsync(
                new ResumeShiftCommand(shiftId, locationId), cancellationToken).ConfigureAwait(false),
            _ => Result<CashierShiftId>.Failure(Error.Validation(
                "sync.event_type_unknown", "The event type is not supported by the shift replay handler.")),
        };
    }

    private async Task<Result<Guid>> ReplaySaleAsync(
        SyncPushEvent item,
        CancellationToken cancellationToken)
    {
        SaleSyncPayload? payload;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<SaleSyncPayload>(item.PayloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return Result<Guid>.Failure(Error.Validation(
                "sync.payload_invalid", "The sale event payload is invalid."));
        }

        if (payload is null
            || payload.LocationId == Guid.Empty
            || payload.CashierShiftId == Guid.Empty
            || payload.DeviceId == Guid.Empty
            || payload.CashierId == Guid.Empty
            || payload.Lines.Count == 0
            || payload.Payments.Count == 0)
        {
            return Result<Guid>.Failure(Error.Validation(
                "sync.payload_invalid", "The sale event payload is incomplete."));
        }

        Result<DocumentNumber> number = DocumentNumber.Parse(payload.Number);
        if (number.IsFailure)
        {
            return Result<Guid>.Failure(number.Errors);
        }

        // A register that sent this sale online and never heard back queues it
        // under the same event identity. If that first attempt did commit, the
        // sale is already here: it is the same sale, not a second one.
        SaleId? existing = await context.Sales
            .AsNoTracking()
            .Where(s => s.EventId == new EventId(item.EventId))
            .Select(s => (SaleId?)s.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is { } alreadyPosted)
        {
            return Result<Guid>.Success(alreadyPosted.Value);
        }

        CompleteSaleLine[] lines =
        [
            .. payload.Lines.Select(line => new CompleteSaleLine(
                new ProductId(line.ProductId),
                line.Quantity,
                new UnitOfMeasureId(line.UnitOfMeasureId),
                line.Barcode,
                line.UnitPriceOverride,
                line.PriceOverrideAuthorizedByUserId is { } priceAuthorizer ? new UserId(priceAuthorizer) : null,
                line.Discount,
                line.DiscountAuthorizedByUserId is { } discountAuthorizer ? new UserId(discountAuthorizer) : null,
                line.AllowExpiredOverride,
                line.ExpiredOverrideReason)),
        ];

        CompleteSalePayment[] payments =
        [.. payload.Payments.Select(payment => new CompleteSalePayment(
            payment.Method,
            payment.Amount,
            payment.Tendered,
            payment.ProviderReference))];

        Result<SaleId> replay = await dispatcher.SendAsync(
            new CompleteSaleCommand(
                number.Value,
                new EventId(item.EventId),
                new LocationId(payload.LocationId),
                new CashierShiftId(payload.CashierShiftId),
                new DeviceId(payload.DeviceId),
                new UserId(payload.CashierId),
                payload.CustomerId is { } customerId ? new CustomerId(customerId) : null,
                payload.BusinessDate,
                payload.CompletedAtUtc,
                lines,
                payments),
            cancellationToken).ConfigureAwait(false);

        return replay.IsSuccess
            ? Result<Guid>.Success(replay.Value.Value)
            : Result<Guid>.Failure(replay.Errors);
    }

    private async Task<Result<CashierShiftId>> ReplayOpenAsync(
        ShiftSyncPayload payload,
        LocationId locationId,
        CashierShiftId shiftId,
        CancellationToken cancellationToken)
    {
        Result<DocumentNumber> number = DocumentNumber.Parse(payload.Number);
        if (number.IsFailure)
        {
            return Result<CashierShiftId>.Failure(number.Errors);
        }

        return await dispatcher.SendAsync(
            new OpenShiftCommand(
                number.Value,
                locationId,
                payload.BusinessDate,
                payload.OpeningFloat,
                shiftId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordProcessedAsync(
        SyncPushEvent item,
        Guid deviceId,
        SyncDeviceCheckpoint checkpoint,
        bool isNewCheckpoint,
        SyncProcessingOutcome outcome,
        DateTimeOffset now,
        string responseJson,
        CancellationToken cancellationToken)
    {
        context.ProcessedSyncEvents.Add(new ProcessedSyncEvent(
            new EventId(item.EventId),
            new DeviceId(deviceId),
            item.DeviceSequence,
            item.EventType,
            item.PayloadJson,
            Convert.FromBase64String(item.PayloadHash),
            new UserId(item.UserId),
            new LocationId(item.LocationId),
            item.OccurredAtUtc,
            item.DeviceUptimeTicks,
            new CorrelationId(item.CorrelationId == Guid.Empty ? Guid.CreateVersion7() : item.CorrelationId),
            outcome,
            now,
            responseJson));

        if (isNewCheckpoint)
        {
            context.SyncDeviceCheckpoints.Add(checkpoint);
        }

        checkpoint.Advance(item.DeviceSequence, now);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsShiftEvent(string eventType)
        => eventType is "ShiftOpened" or "ShiftSuspended" or "ShiftResumed";

    private static bool IsReplayableEvent(string eventType)
        => IsShiftEvent(eventType) || eventType is "SaleCompleted" or "TransferReceived";
}

public sealed record SyncPushRequest(Guid DeviceId, DateTimeOffset ClientSentAtUtc, long DeviceUptimeTicks, IReadOnlyList<SyncPushEvent> Events);

public sealed record SyncPushEvent(
    Guid EventId,
    long DeviceSequence,
    string EventType,
    string PayloadJson,
    string PayloadHash,
    Guid UserId,
    Guid LocationId,
    DateTimeOffset OccurredAtUtc,
    long DeviceUptimeTicks,
    Guid CorrelationId);

public sealed record SyncPushEventResult(
    Guid EventId,
    string Outcome,
    DateTimeOffset? AppliedAtUtc,
    string? ErrorCode,
    string? Message,
    string? Remediation = null)
{
    public static SyncPushEventResult Rejected(Guid id, string code, string message, string? remediation = null)
        => new(id, "Rejected", null, code, message, remediation);

    public static SyncPushEventResult Deferred(Guid id, string code, string message)
        => new(id, "Deferred", null, code, message);

    public static SyncPushEventResult Conflict(Guid id, string code, string message)
        => new(id, "Conflict", null, code, message);

    public static SyncPushEventResult Duplicate(Guid id, SyncProcessingOutcome outcome, DateTimeOffset appliedAtUtc)
        => new(id, outcome == SyncProcessingOutcome.RequiresReview ? "RequiresReview" : outcome.ToString(), appliedAtUtc, null, null);

    public static SyncPushEventResult Parked(Guid id, DateTimeOffset appliedAtUtc)
        => new(id, "RequiresReview", appliedAtUtc, "sync.handler_unavailable", "The event was recorded for server-side review.");

    public static SyncPushEventResult RequiresReview(Guid id, DateTimeOffset appliedAtUtc, string code, string message)
        => new(id, "RequiresReview", appliedAtUtc, code, message);

    public static SyncPushEventResult Accepted(Guid id, DateTimeOffset appliedAtUtc)
        => new(id, "Accepted", appliedAtUtc, null, null);
}

public sealed record SyncPushResponse(
    DateTimeOffset ServerReceivedAtUtc,
    IReadOnlyList<SyncPushEventResult> Results,
    long NextCursor,
    string? ErrorCode,
    string? Message)
{
    public static SyncPushResponse Refused(string code, string message)
        => new(DateTimeOffset.UtcNow, [], 0, code, message);
}
