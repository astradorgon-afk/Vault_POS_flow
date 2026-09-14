using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Inventory;

/// <summary>
/// Handles <see cref="CreateStockAdjustmentCommand"/>: validates products and
/// batches, captures each line's unit cost from the bucket, and stages the draft.
/// </summary>
public sealed class CreateStockAdjustmentCommandHandler(
    IInventoryControlRepository repository,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CreateStockAdjustmentCommand, StockAdjustmentId>
{
    /// <inheritdoc />
    public async Task<Result<StockAdjustmentId>> HandleAsync(
        CreateStockAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        InventoryControlLocation? location = await repository
            .GetLocationAsync(command.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null || location.Kind == Domain.Locations.LocationKind.External)
        {
            return Result<StockAdjustmentId>.Failure(InventoryControlErrors.LocationInvalid(command.LocationId));
        }

        Result<Dictionary<ProductId, Product>> products = await InventoryControlSupport
            .ProductsAsync(repository, command.Lines.Select(l => l.ProductId), cancellationToken)
            .ConfigureAwait(false);

        if (products.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(products.Errors);
        }

        Result<Dictionary<BatchId, Batch>> batches = await InventoryControlSupport
            .BatchesAsync(repository, [.. command.Lines.Select(l => (l.ProductId, l.BatchId))], cancellationToken)
            .ConfigureAwait(false);

        if (batches.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(batches.Errors);
        }

        IReadOnlyList<BucketSnapshot> buckets = await repository
            .GetBucketsAsync(
                command.LocationId,
                products.Value.Keys,
                [.. command.Lines.Select(l => l.State).Distinct()],
                cancellationToken)
            .ConfigureAwait(false);

        List<StockAdjustmentLineSpec> specs = [.. command.Lines.Select(line =>
        {
            Product product = products.Value[line.ProductId];
            BatchId batchKey = line.BatchId ?? BatchId.Empty;
            BucketSnapshot? bucket = buckets.FirstOrDefault(b =>
                b.ProductId == line.ProductId && b.BatchKey == batchKey && b.State == line.State);
            Batch? batch = line.BatchId is { } id ? batches.Value.GetValueOrDefault(id) : null;

            return new StockAdjustmentLineSpec(
                line.ProductId,
                line.BatchId,
                line.State,
                line.QuantityDelta,
                InventoryControlSupport.UnitCost(bucket, batch, product),
                product.TracksBatches);
        })];

        Result<StockAdjustment> created = StockAdjustment.Create(
            command.LocationId,
            command.Reason,
            command.Notes,
            specs,
            currentUser.UserId ?? UserId.Empty,
            clock.UtcNow);

        if (created.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(created.Errors);
        }

        StockAdjustment adjustment = created.Value;
        repository.AddAdjustment(adjustment);

        await InventoryControlSupport.AuditAsync(
                audit, AuditActions.Inventory.AdjustmentCreated, nameof(StockAdjustment), adjustment.Id.Value,
                adjustment.LocationId,
                new { Reason = adjustment.Reason.ToString(), Lines = adjustment.Lines.Count, adjustment.TotalAbsoluteValue },
                adjustment.Notes, ReferenceDocumentType.StockAdjustment, cancellationToken)
            .ConfigureAwait(false);

        return Result<StockAdjustmentId>.Success(adjustment.Id);
    }
}

/// <summary>Handles <see cref="SubmitStockAdjustmentCommand"/>.</summary>
public sealed class SubmitStockAdjustmentCommandHandler(
    IInventoryControlRepository repository,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<SubmitStockAdjustmentCommand, StockAdjustmentId>
{
    /// <inheritdoc />
    public async Task<Result<StockAdjustmentId>> HandleAsync(
        SubmitStockAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        StockAdjustment? adjustment = await repository
            .GetAdjustmentAsync(command.AdjustmentId, cancellationToken)
            .ConfigureAwait(false);

        if (adjustment is null)
        {
            return Result<StockAdjustmentId>.Failure(InventoryControlErrors.AdjustmentUnknown(command.AdjustmentId));
        }

        if (!await InventoryControlSupport
                .InScopeAsync(permissions, currentUser, Permissions.Inventory.Adjust, adjustment.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<StockAdjustmentId>.Failure(InventoryControlErrors.OutsideScope);
        }

        Result submitted = adjustment.Submit(clock.UtcNow);

        return submitted.IsSuccess
            ? Result<StockAdjustmentId>.Success(adjustment.Id)
            : Result<StockAdjustmentId>.Failure(submitted.Errors);
    }
}

/// <summary>
/// Handles <see cref="ApproveStockAdjustmentCommand"/>: checks the approver's tier
/// against the adjustment's value (never the author's own), allocates the ADJ
/// number and posts one ledger group per movement type.
/// </summary>
public sealed class ApproveStockAdjustmentCommandHandler(
    IInventoryControlRepository repository,
    IApprovalGate gate,
    IInventoryLedger ledger,
    IDocumentNumberGenerator numbers,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ApproveStockAdjustmentCommand, StockAdjustmentId>
{
    /// <inheritdoc />
    public async Task<Result<StockAdjustmentId>> HandleAsync(
        ApproveStockAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        StockAdjustment? adjustment = await repository
            .GetAdjustmentAsync(command.AdjustmentId, cancellationToken)
            .ConfigureAwait(false);

        if (adjustment is null)
        {
            return Result<StockAdjustmentId>.Failure(InventoryControlErrors.AdjustmentUnknown(command.AdjustmentId));
        }

        if (adjustment.Status != StockAdjustmentStatus.PendingApproval)
        {
            return Result<StockAdjustmentId>.Failure(
                InventoryControlErrors.AdjustmentInvalidState(StockAdjustmentStatus.PendingApproval, adjustment.Status));
        }

        UserId approver = currentUser.UserId ?? UserId.Empty;

        Result allowed = await gate
            .RequireAsync(
                Permissions.Inventory.ApproveAdjustment,
                adjustment.TotalAbsoluteValue,
                approver,
                adjustment.CreatedByUserId,
                adjustment.LocationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (allowed.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(allowed.Errors);
        }

        Result<PostingLocation> location = await InventoryControlSupport
            .PostingLocationAsync(repository, adjustment.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(location.Errors);
        }

        Result<Dictionary<ProductId, Product>> products = await InventoryControlSupport
            .ProductsAsync(repository, adjustment.Lines.Select(l => l.ProductId), cancellationToken)
            .ConfigureAwait(false);

        if (products.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(products.Errors);
        }

        DocumentNumber number = await numbers
            .NextAsync(DocumentType.StockAdjustment, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;

        foreach (IGrouping<InventoryMovementType, StockAdjustmentLine> group in adjustment.Lines.GroupBy(l => l.MovementType))
        {
            MovementGroupSpec spec = new(
                EventId: EventId.New(),
                MovementType: group.Key,
                ReferenceDocumentType: ReferenceDocumentType.StockAdjustment,
                ReferenceDocumentId: adjustment.Id.Value,
                ReferenceNumber: number.Value,
                Legs: InventoryControlSupport.Legs(
                    group.Key,
                    group.Select(l => new BucketChange(l.ProductId, l.BatchId, l.State, l.QuantityDelta, l.UnitCost)),
                    location.Value,
                    products.Value),
                Actor: new LedgerActor(adjustment.CreatedByUserId, approver, currentUser.DeviceId, currentUser.CorrelationId),
                OccurredAtUtc: now,
                BusinessDate: clock.BusinessDateFor(location.Value.TimeZoneId),
                ReasonCode: adjustment.Reason,
                Notes: adjustment.Notes);

            Result<PostedMovementGroup> posted = await ledger.PostAsync(spec, cancellationToken).ConfigureAwait(false);

            if (posted.IsFailure)
            {
                return Result<StockAdjustmentId>.Failure(posted.Errors);
            }
        }

        Result approved = adjustment.Post(number, approver, now);

        if (approved.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(approved.Errors);
        }

        await InventoryControlSupport.AuditAsync(
                audit, AuditActions.Inventory.AdjustmentApproved, nameof(StockAdjustment), adjustment.Id.Value,
                adjustment.LocationId, new { adjustment.Number, adjustment.TotalAbsoluteValue }, reason: null,
                ReferenceDocumentType.StockAdjustment, cancellationToken)
            .ConfigureAwait(false);

        return Result<StockAdjustmentId>.Success(adjustment.Id);
    }
}

/// <summary>Handles <see cref="RejectStockAdjustmentCommand"/>.</summary>
public sealed class RejectStockAdjustmentCommandHandler(
    IInventoryControlRepository repository,
    IPermissionEvaluator permissions,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RejectStockAdjustmentCommand, StockAdjustmentId>
{
    /// <inheritdoc />
    public async Task<Result<StockAdjustmentId>> HandleAsync(
        RejectStockAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        StockAdjustment? adjustment = await repository
            .GetAdjustmentAsync(command.AdjustmentId, cancellationToken)
            .ConfigureAwait(false);

        if (adjustment is null)
        {
            return Result<StockAdjustmentId>.Failure(InventoryControlErrors.AdjustmentUnknown(command.AdjustmentId));
        }

        if (!await InventoryControlSupport
                .InScopeAsync(permissions, currentUser, Permissions.Inventory.ApproveAdjustment, adjustment.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<StockAdjustmentId>.Failure(InventoryControlErrors.OutsideScope);
        }

        Result rejected = adjustment.Reject(currentUser.UserId ?? UserId.Empty, command.Reason, clock.UtcNow);

        if (rejected.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(rejected.Errors);
        }

        await InventoryControlSupport.AuditAsync(
                audit, AuditActions.Inventory.AdjustmentRejected, nameof(StockAdjustment), adjustment.Id.Value,
                adjustment.LocationId, details: null, command.Reason, ReferenceDocumentType.StockAdjustment, cancellationToken)
            .ConfigureAwait(false);

        return Result<StockAdjustmentId>.Success(adjustment.Id);
    }
}

/// <summary>
/// Handles <see cref="ReverseStockAdjustmentCommand"/>: posts a reversal group for
/// every group the adjustment posted, each leg negated at its original cost, under
/// the same approval-tier check as approving it.
/// </summary>
public sealed class ReverseStockAdjustmentCommandHandler(
    IInventoryControlRepository repository,
    IApprovalGate gate,
    IInventoryLedger ledger,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ReverseStockAdjustmentCommand, StockAdjustmentId>
{
    /// <inheritdoc />
    public async Task<Result<StockAdjustmentId>> HandleAsync(
        ReverseStockAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        StockAdjustment? adjustment = await repository
            .GetAdjustmentAsync(command.AdjustmentId, cancellationToken)
            .ConfigureAwait(false);

        if (adjustment is null)
        {
            return Result<StockAdjustmentId>.Failure(InventoryControlErrors.AdjustmentUnknown(command.AdjustmentId));
        }

        UserId reverser = currentUser.UserId ?? UserId.Empty;
        DateTimeOffset now = clock.UtcNow;

        Result reversed = adjustment.Reverse(reverser, command.Reason, now);

        if (reversed.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(reversed.Errors);
        }

        Result allowed = await gate
            .RequireAsync(
                Permissions.Inventory.ApproveAdjustment,
                adjustment.TotalAbsoluteValue,
                reverser,
                adjustment.CreatedByUserId,
                adjustment.LocationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (allowed.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(allowed.Errors);
        }

        Result<PostingLocation> location = await InventoryControlSupport
            .PostingLocationAsync(repository, adjustment.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(location.Errors);
        }

        IReadOnlyList<PostedLeg> legs = await repository
            .GetPostedLegsAsync(ReferenceDocumentType.StockAdjustment, adjustment.Id.Value, cancellationToken)
            .ConfigureAwait(false);

        Result<Dictionary<ProductId, Product>> products = await InventoryControlSupport
            .ProductsAsync(repository, legs.Select(l => l.ProductId), cancellationToken)
            .ConfigureAwait(false);

        if (products.IsFailure)
        {
            return Result<StockAdjustmentId>.Failure(products.Errors);
        }

        foreach (IGrouping<MovementGroupId, PostedLeg> group in legs.GroupBy(l => l.MovementGroupId))
        {
            MovementGroupSpec spec = new(
                EventId: EventId.New(),
                MovementType: InventoryMovementType.Reversal,
                ReferenceDocumentType: ReferenceDocumentType.StockAdjustment,
                ReferenceDocumentId: adjustment.Id.Value,
                ReferenceNumber: adjustment.Number,
                Legs: [.. group.Select(leg => new MovementLegSpec(
                    leg.ProductId,
                    leg.BatchId,
                    leg.LocationId,
                    leg.LocationId == location.Value.LocationId ? location.Value.Kind : Domain.Locations.LocationKind.External,
                    leg.State,
                    -leg.QuantityDelta,
                    leg.UnitCost,
                    products.Value[leg.ProductId].TracksBatches))],
                Actor: new LedgerActor(reverser, reverser, currentUser.DeviceId, currentUser.CorrelationId),
                OccurredAtUtc: now,
                BusinessDate: clock.BusinessDateFor(location.Value.TimeZoneId),
                ReasonCode: adjustment.Reason,
                Notes: adjustment.ReversalReason,
                ReversesMovementGroupId: group.Key);

            Result<PostedMovementGroup> posted = await ledger.PostAsync(spec, cancellationToken).ConfigureAwait(false);

            if (posted.IsFailure)
            {
                return Result<StockAdjustmentId>.Failure(posted.Errors);
            }
        }

        await InventoryControlSupport.AuditAsync(
                audit, AuditActions.Inventory.AdjustmentReversed, nameof(StockAdjustment), adjustment.Id.Value,
                adjustment.LocationId, new { adjustment.Number, adjustment.TotalAbsoluteValue }, command.Reason,
                ReferenceDocumentType.StockAdjustment, cancellationToken)
            .ConfigureAwait(false);

        return Result<StockAdjustmentId>.Success(adjustment.Id);
    }
}
