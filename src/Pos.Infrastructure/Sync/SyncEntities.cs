using Pos.Domain.Common;

namespace Pos.Infrastructure.Sync;

/// <summary>The server-side outcome retained for every uploaded event.</summary>
public enum SyncProcessingOutcome
{
    Accepted = 1,
    Duplicate = 2,
    Rejected = 3,
    RequiresReview = 4,
    Conflict = 5,
}

/// <summary>Lifecycle of a sync failure awaiting an operator or retry.</summary>
public enum SyncFailureStatus
{
    Pending = 1,
    RetryScheduled = 2,
    Resolved = 3,
    Dismissed = 4,
}

/// <summary>A durable operator-visible sync failure.</summary>
public sealed class SyncFailure
{
    private SyncFailure() { ErrorCode = string.Empty; ErrorMessage = string.Empty; }

    public SyncFailure(
        EventId eventId,
        DeviceId deviceId,
        string errorCode,
        string errorMessage,
        DateTimeOffset createdAtUtc)
    {
        Id = Guid.CreateVersion7();
        EventId = eventId;
        DeviceId = deviceId;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        CreatedAtUtc = createdAtUtc;
        Status = SyncFailureStatus.Pending;
    }

    public Guid Id { get; private init; }
    public EventId EventId { get; private init; }
    public DeviceId DeviceId { get; private init; }
    public string ErrorCode { get; private init; }
    public string ErrorMessage { get; private init; }
    public SyncFailureStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private init; }
    public DateTimeOffset? LastAttemptAtUtc { get; private set; }
    public DateTimeOffset? NextRetryAtUtc { get; private set; }
    public DateTimeOffset? ResolvedAtUtc { get; private set; }
    public string? ResolutionNote { get; private set; }

    public void ScheduleRetry(DateTimeOffset now)
    {
        if (Status is SyncFailureStatus.Resolved or SyncFailureStatus.Dismissed)
        {
            throw new InvalidOperationException("A resolved sync failure cannot be retried.");
        }

        AttemptCount++;
        LastAttemptAtUtc = now;
        NextRetryAtUtc = now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(AttemptCount, 6))));
        Status = SyncFailureStatus.RetryScheduled;
    }

    public void Dismiss(DateTimeOffset now, string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        Status = SyncFailureStatus.Dismissed;
        ResolvedAtUtc = now;
        ResolutionNote = note.Trim();
    }

    public void Resolve(DateTimeOffset now, string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        Status = SyncFailureStatus.Resolved;
        ResolvedAtUtc = now;
        ResolutionNote = note.Trim();
        NextRetryAtUtc = null;
    }
}

/// <summary>One idempotently processed device event.</summary>
public sealed class ProcessedSyncEvent
{
    private ProcessedSyncEvent() { EventType = string.Empty; PayloadJson = string.Empty; PayloadHash = []; }

    public ProcessedSyncEvent(
        EventId eventId,
        DeviceId deviceId,
        long deviceSequence,
        string eventType,
        string payloadJson,
        byte[] payloadHash,
        UserId userId,
        LocationId locationId,
        DateTimeOffset occurredAtUtc,
        long deviceUptimeTicks,
        CorrelationId correlationId,
        SyncProcessingOutcome outcome,
        DateTimeOffset appliedAtUtc,
        string? responseJson = null)
    {
        EventId = eventId;
        DeviceId = deviceId;
        DeviceSequence = deviceSequence;
        EventType = eventType;
        PayloadJson = payloadJson;
        PayloadHash = payloadHash;
        UserId = userId;
        LocationId = locationId;
        OccurredAtUtc = occurredAtUtc;
        DeviceUptimeTicks = deviceUptimeTicks;
        CorrelationId = correlationId;
        Outcome = outcome;
        AppliedAtUtc = appliedAtUtc;
        ResponseJson = responseJson;
    }

    public EventId EventId { get; private init; }
    public DeviceId DeviceId { get; private init; }
    public long DeviceSequence { get; private init; }
    public string EventType { get; private init; }
    public string PayloadJson { get; private init; }
    public byte[] PayloadHash { get; private init; }
    public UserId UserId { get; private init; }
    public LocationId LocationId { get; private init; }
    public DateTimeOffset OccurredAtUtc { get; private init; }
    public long DeviceUptimeTicks { get; private init; }
    public CorrelationId CorrelationId { get; private init; }
    public SyncProcessingOutcome Outcome { get; private set; }
    public DateTimeOffset AppliedAtUtc { get; private set; }
    public string? ResponseJson { get; private set; }

    public void MarkAccepted(DateTimeOffset appliedAtUtc, string responseJson)
    {
        Outcome = SyncProcessingOutcome.Accepted;
        AppliedAtUtc = appliedAtUtc;
        ResponseJson = responseJson;
    }
}

/// <summary>The last contiguous event sequence accepted for a device.</summary>
public sealed class SyncDeviceCheckpoint
{
    private SyncDeviceCheckpoint() { }

    public SyncDeviceCheckpoint(DeviceId deviceId, long lastAcceptedSequence, DateTimeOffset updatedAtUtc)
    {
        DeviceId = deviceId;
        LastAcceptedSequence = lastAcceptedSequence;
        UpdatedAtUtc = updatedAtUtc;
    }

    public DeviceId DeviceId { get; private init; }
    public long LastAcceptedSequence { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? GapDetectedAtUtc { get; private set; }

    public void Advance(long sequence, DateTimeOffset atUtc)
    {
        if (sequence != LastAcceptedSequence + 1)
        {
            throw new InvalidOperationException("A synchronization checkpoint advances contiguously.");
        }

        LastAcceptedSequence = sequence;
        UpdatedAtUtc = atUtc;
        GapDetectedAtUtc = null;
    }

    public void MarkGapDetected(DateTimeOffset atUtc)
        => GapDetectedAtUtc ??= atUtc;

    public bool GapHasExpired(DateTimeOffset now, TimeSpan timeout)
        => GapDetectedAtUtc is { } detected && now - detected >= timeout;

    public void SkipToAfterExpiredGap(long sequence, DateTimeOffset atUtc)
    {
        if (sequence <= LastAcceptedSequence)
        {
            throw new InvalidOperationException("An expired gap can only skip forward.");
        }

        LastAcceptedSequence = sequence;
        UpdatedAtUtc = atUtc;
        GapDetectedAtUtc = null;
    }
}
