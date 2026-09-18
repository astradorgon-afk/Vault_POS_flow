using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>Lists and schedules operator actions for synchronization failures.</summary>
public sealed class SyncFailureService(
    PosDbContext context,
    ISystemClock clock)
{
    public async Task<IReadOnlyList<Guid>> GetDueFailureIdsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
        => await context.SyncFailures
            .Where(f => f.Status == SyncFailureStatus.RetryScheduled
                        && f.NextRetryAtUtc <= now
                        && f.ErrorCode != "sync.handler_unavailable")
            .OrderBy(f => f.NextRetryAtUtc)
            .Select(f => f.Id)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    public async Task<IReadOnlyList<SyncFailureView>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        int bounded = Math.Clamp(limit == 0 ? 100 : limit, 1, 500);
        return await context.SyncFailures
            .AsNoTracking()
            .Where(f => f.Status != SyncFailureStatus.Dismissed && f.Status != SyncFailureStatus.Resolved)
            .OrderBy(f => f.NextRetryAtUtc ?? f.CreatedAtUtc)
            .Take(bounded)
            .Select(f => new SyncFailureView(
                f.Id, f.EventId.Value, f.DeviceId.Value, f.ErrorCode, f.ErrorMessage,
                f.Status.ToString(), f.AttemptCount, f.NextRetryAtUtc, f.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<SyncFailureView>> RetryAsync(
        Guid failureId,
        CancellationToken cancellationToken)
    {
        SyncFailure? failure = await context.SyncFailures
            .SingleOrDefaultAsync(f => f.Id == failureId, cancellationToken)
            .ConfigureAwait(false);

        if (failure is null)
        {
            return Result<SyncFailureView>.Failure(Error.NotFound("sync.failure_unknown", "The synchronization failure was not found."));
        }

        try
        {
            failure.ScheduleRetry(clock.UtcNow);
        }
        catch (InvalidOperationException)
        {
            return Result<SyncFailureView>.Failure(Error.Conflict("sync.failure_closed", "The synchronization failure is already closed."));
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<SyncFailureView>.Success(ToView(failure));
    }

    public async Task<Result<SyncFailureView>> DismissAsync(
        Guid failureId,
        string note,
        CancellationToken cancellationToken)
    {
        SyncFailure? failure = await context.SyncFailures
            .SingleOrDefaultAsync(f => f.Id == failureId, cancellationToken)
            .ConfigureAwait(false);

        if (failure is null)
        {
            return Result<SyncFailureView>.Failure(Error.NotFound("sync.failure_unknown", "The synchronization failure was not found."));
        }

        try
        {
            failure.Dismiss(clock.UtcNow, note);
        }
        catch (ArgumentException)
        {
            return Result<SyncFailureView>.Failure(Error.Validation("sync.dismiss_note_required", "A dismissal note is required."));
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<SyncFailureView>.Success(ToView(failure));
    }

    private static SyncFailureView ToView(SyncFailure failure)
        => new(
            failure.Id,
            failure.EventId.Value,
            failure.DeviceId.Value,
            failure.ErrorCode,
            failure.ErrorMessage,
            failure.Status.ToString(),
            failure.AttemptCount,
            failure.NextRetryAtUtc,
            failure.CreatedAtUtc);
}

public sealed record SyncFailureView(
    Guid Id,
    Guid EventId,
    Guid DeviceId,
    string ErrorCode,
    string ErrorMessage,
    string Status,
    int AttemptCount,
    DateTimeOffset? NextRetryAtUtc,
    DateTimeOffset CreatedAtUtc);
