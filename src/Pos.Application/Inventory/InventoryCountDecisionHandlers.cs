using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Inventory;

/// <summary>
/// Handles <see cref="ApproveInventoryCountCommand"/>: checks the approver's tier
/// against the absolute variance value, refuses lines whose stock moved after they
/// were counted, and posts the variance as count adjustments under the CNT number.
/// </summary>
public sealed class ApproveInventoryCountCommandHandler(
    IInventoryControlRepository repository,
    IApprovalGate gate,
    IInventoryLedger ledger,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ApproveInventoryCountCommand, InventoryCountId>
{
    /// <inheritdoc />
    public async Task<Result<InventoryCountId>> HandleAsync(
        ApproveInventoryCountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        InventoryCount? count = await repository.GetCountAsync(command.CountId, cancellationToken).ConfigureAwait(false);

        if (count is null)
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.CountUnknown(command.CountId));
        }

        if (count.Status != InventoryCountStatus.PendingApproval)
        {
            return Result<InventoryCountId>.Failure(
                InventoryControlErrors.CountInvalidState(InventoryCountStatus.PendingApproval, count.Status));
        }

        UserId approver = currentUser.UserId ?? UserId.Empty;

        // The person whose counting produced the variance may not approve it.
        Result allowed = await gate
            .RequireAsync(
                Permissions.Inventory.ApproveCount,
                count.TotalAbsoluteVarianceValue,
                approver,
                count.SubmittedByUserId ?? count.CreatedByUserId,
                count.LocationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (allowed.IsFailure)
        {
            return Result<InventoryCountId>.Failure(allowed.Errors);
        }

        Result<PostingLocation> location = await InventoryControlSupport
            .PostingLocationAsync(repository, count.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location.IsFailure)
        {
            return Result<InventoryCountId>.Failure(location.Errors);
        }

        IReadOnlyList<BucketSnapshot> buckets = await repository
            .GetBucketsAsync(
                count.LocationId,
                [.. count.Lines.Select(l => l.ProductId).Distinct()],
                [InventoryState.Available],
                cancellationToken)
            .ConfigureAwait(false);

        int[] stale = [.. count.Lines
            .Where(line => (buckets.FirstOrDefault(b =>
                    b.ProductId == line.ProductId && b.BatchKey == (line.BatchId ?? BatchId.Empty))?.Quantity ?? 0m)
                != line.SystemQuantity)
            .Select(line => line.LineNo)];

        if (stale.Length > 0)
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.CountStale(stale));
        }

        Result<Dictionary<ProductId, Product>> products = await InventoryControlSupport
            .ProductsAsync(repository, count.Lines.Select(l => l.ProductId), cancellationToken)
            .ConfigureAwait(false);

        if (products.IsFailure)
        {
            return Result<InventoryCountId>.Failure(products.Errors);
        }

        DateTimeOffset now = clock.UtcNow;

        foreach ((InventoryMovementType type, Func<decimal, bool> selects) in new (InventoryMovementType, Func<decimal, bool>)[]
                 {
                     (InventoryMovementType.CountAdjustmentIncrease, variance => variance > 0m),
                     (InventoryMovementType.CountAdjustmentDecrease, variance => variance < 0m),
                 })
        {
            List<BucketChange> changes = [.. count.Lines
                .Where(l => selects(l.Variance ?? 0m))
                .Select(l => new BucketChange(l.ProductId, l.BatchId, InventoryState.Available, l.Variance!.Value, l.UnitCost))];

            if (changes.Count == 0)
            {
                continue;
            }

            MovementGroupSpec spec = new(
                EventId: EventId.New(),
                MovementType: type,
                ReferenceDocumentType: ReferenceDocumentType.InventoryCount,
                ReferenceDocumentId: count.Id.Value,
                ReferenceNumber: count.Number,
                Legs: InventoryControlSupport.Legs(type, changes, location.Value, products.Value),
                Actor: new LedgerActor(
                    count.SubmittedByUserId ?? count.CreatedByUserId, approver, currentUser.DeviceId, currentUser.CorrelationId),
                OccurredAtUtc: now,
                BusinessDate: clock.BusinessDateFor(location.Value.TimeZoneId),
                ReasonCode: AdjustmentReasonCode.CountCorrection,
                Notes: count.Note);

            Result<PostedMovementGroup> posted = await ledger.PostAsync(spec, cancellationToken).ConfigureAwait(false);

            if (posted.IsFailure)
            {
                return Result<InventoryCountId>.Failure(posted.Errors);
            }
        }

        Result approved = count.Post(approver, now);

        if (approved.IsFailure)
        {
            return Result<InventoryCountId>.Failure(approved.Errors);
        }

        await InventoryControlSupport.AuditAsync(
                audit, AuditActions.Inventory.CountApproved, nameof(InventoryCount), count.Id.Value, count.LocationId,
                new { count.Number, count.TotalAbsoluteVarianceValue }, reason: null,
                ReferenceDocumentType.InventoryCount, cancellationToken)
            .ConfigureAwait(false);

        return Result<InventoryCountId>.Success(count.Id);
    }
}

/// <summary>Handles <see cref="RejectInventoryCountCommand"/>: the count returns to counting.</summary>
public sealed class RejectInventoryCountCommandHandler(
    IInventoryControlRepository repository,
    IPermissionEvaluator permissions,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<RejectInventoryCountCommand, InventoryCountId>
{
    /// <inheritdoc />
    public async Task<Result<InventoryCountId>> HandleAsync(
        RejectInventoryCountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        InventoryCount? count = await repository.GetCountAsync(command.CountId, cancellationToken).ConfigureAwait(false);

        if (count is null)
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.CountUnknown(command.CountId));
        }

        if (!await InventoryControlSupport
                .InScopeAsync(permissions, currentUser, Permissions.Inventory.ApproveCount, count.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.OutsideScope);
        }

        Result rejected = count.Reject(command.Reason);

        if (rejected.IsFailure)
        {
            return Result<InventoryCountId>.Failure(rejected.Errors);
        }

        await InventoryControlSupport.AuditAsync(
                audit, AuditActions.Inventory.CountRejected, nameof(InventoryCount), count.Id.Value, count.LocationId,
                new { count.Number }, command.Reason, ReferenceDocumentType.InventoryCount, cancellationToken)
            .ConfigureAwait(false);

        return Result<InventoryCountId>.Success(count.Id);
    }
}

/// <summary>Handles <see cref="CancelInventoryCountCommand"/>.</summary>
public sealed class CancelInventoryCountCommandHandler(
    IInventoryControlRepository repository,
    IPermissionEvaluator permissions,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<CancelInventoryCountCommand, InventoryCountId>
{
    /// <inheritdoc />
    public async Task<Result<InventoryCountId>> HandleAsync(
        CancelInventoryCountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        InventoryCount? count = await repository.GetCountAsync(command.CountId, cancellationToken).ConfigureAwait(false);

        if (count is null)
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.CountUnknown(command.CountId));
        }

        if (!await InventoryControlSupport
                .InScopeAsync(permissions, currentUser, Permissions.Inventory.Count, count.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.OutsideScope);
        }

        Result cancelled = count.Cancel(command.Reason);

        if (cancelled.IsFailure)
        {
            return Result<InventoryCountId>.Failure(cancelled.Errors);
        }

        await InventoryControlSupport.AuditAsync(
                audit, AuditActions.Inventory.CountCancelled, nameof(InventoryCount), count.Id.Value, count.LocationId,
                new { count.Number }, command.Reason, ReferenceDocumentType.InventoryCount, cancellationToken)
            .ConfigureAwait(false);

        return Result<InventoryCountId>.Success(count.Id);
    }
}
