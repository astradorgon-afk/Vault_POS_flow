using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Sync;

namespace Pos.Domain.Inventory;

/// <summary>Who is posting a movement, and under whose authority.</summary>
/// <param name="CreatedBy">The user performing the action.</param>
/// <param name="ApprovedBy">The approving user, where the movement type requires one.</param>
/// <param name="Device">The device the action originated from, if any.</param>
/// <param name="Correlation">The correlation identifier of the wider operation.</param>
public sealed record LedgerActor(
    UserId CreatedBy,
    UserId? ApprovedBy,
    DeviceId? Device,
    CorrelationId Correlation);

/// <summary>One intended leg of an inventory event.</summary>
/// <param name="ProductId">The product affected.</param>
/// <param name="BatchId">The batch affected, when the product is batch-tracked.</param>
/// <param name="LocationId">The location whose bucket changes.</param>
/// <param name="LocationKind">The kind of that location, used to validate the state.</param>
/// <param name="State">The state whose bucket changes.</param>
/// <param name="QuantityDelta">The signed quantity change. Must not be zero.</param>
/// <param name="UnitCost">The valuation cost per unit. Must not be negative.</param>
/// <param name="ProductTracksBatches">Whether the product requires a batch.</param>
public sealed record MovementLegSpec(
    ProductId ProductId,
    BatchId? BatchId,
    LocationId LocationId,
    LocationKind LocationKind,
    InventoryState State,
    decimal QuantityDelta,
    decimal UnitCost,
    bool ProductTracksBatches);

/// <summary>A complete inventory event awaiting validation and posting.</summary>
/// <param name="EventId">The globally unique business event identifier, used for idempotency.</param>
/// <param name="MovementType">Why stock is moving.</param>
/// <param name="ReferenceDocumentType">The kind of document justifying the movement.</param>
/// <param name="ReferenceDocumentId">The justifying document's identifier.</param>
/// <param name="ReferenceNumber">The justifying document's human-readable number.</param>
/// <param name="Legs">The legs, which must net to zero per product and batch.</param>
/// <param name="Actor">Who is posting, and under whose authority.</param>
/// <param name="OccurredAtUtc">The business time the movement happened.</param>
/// <param name="BusinessDate">The trading day the movement belongs to.</param>
/// <param name="ReasonCode">The adjustment reason, where required.</param>
/// <param name="Notes">Free-text notes.</param>
/// <param name="ReversesMovementGroupId">The group being reversed, for corrections.</param>
/// <param name="SyncStatus">The upload state, for device-originated events.</param>
/// <param name="ServerProcessingStatus">The server's verdict on the event.</param>
public sealed record MovementGroupSpec(
    EventId EventId,
    InventoryMovementType MovementType,
    ReferenceDocumentType ReferenceDocumentType,
    Guid? ReferenceDocumentId,
    string ReferenceNumber,
    IReadOnlyList<MovementLegSpec> Legs,
    LedgerActor Actor,
    DateTimeOffset OccurredAtUtc,
    DateOnly BusinessDate,
    AdjustmentReasonCode? ReasonCode = null,
    string? Notes = null,
    MovementGroupId? ReversesMovementGroupId = null,
    SyncStatus SyncStatus = SyncStatus.NotApplicable,
    ServerProcessingStatus ServerProcessingStatus = ServerProcessingStatus.Accepted);

/// <summary>
/// A validated, balanced set of movement legs forming one inventory event.
/// </summary>
/// <remarks>
/// This is where the ledger's structural invariants live. Constructing a group
/// is the only way to construct <see cref="InventoryMovement"/> instances, so
/// an unbalanced or illegal movement cannot reach persistence. Stock
/// availability and authorization are checked by the ledger service, which has
/// the balances and the permission evaluator; this type checks everything that
/// can be decided from the event alone.
/// </remarks>
public sealed class InventoryMovementGroup
{
    private const int MinimumNotesLengthForOtherReason = 10;

    private InventoryMovementGroup(
        MovementGroupId id,
        MovementGroupSpec spec,
        IReadOnlyList<InventoryMovement> movements)
    {
        Id = id;
        Spec = spec;
        Movements = movements;
    }

    /// <summary>Gets the identifier shared by every leg in the group.</summary>
    public MovementGroupId Id { get; }

    /// <summary>Gets the specification the group was built from.</summary>
    public MovementGroupSpec Spec { get; }

    /// <summary>Gets the validated legs, in order.</summary>
    public IReadOnlyList<InventoryMovement> Movements { get; }

    /// <summary>Gets the total signed value change, which is zero for pure transfers.</summary>
    public decimal TotalValueDelta => Movements.Sum(m => m.TotalValueDelta);

    /// <summary>Gets the absolute value moved, used for approval thresholds.</summary>
    public decimal AbsoluteValueMoved
        => Movements.Where(m => m.IsIncrease).Sum(m => m.TotalValueDelta);

    /// <summary>
    /// Validates a specification and, if it is sound, materialises the immutable
    /// movement legs.
    /// </summary>
    /// <param name="spec">The intended event.</param>
    /// <param name="recordedAtUtc">
    /// The server clock time to stamp on the legs. Authoritative for ordering;
    /// never taken from the device.
    /// </param>
    /// <returns>The validated group, or the failures that prevented it.</returns>
    public static Result<InventoryMovementGroup> Create(MovementGroupSpec spec, DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(spec);

        List<Error> errors = [];

        if (!MovementTypeRules.IsDefined(spec.MovementType))
        {
            return Result<InventoryMovementGroup>.Failure(InventoryErrors.UnknownMovementType);
        }

        MovementTypeRule rule = MovementTypeRules.For(spec.MovementType);

        ValidateLegs(spec, rule, errors);
        ValidateBalance(spec, errors);
        ValidateJustification(spec, rule, errors);

        if (errors.Count > 0)
        {
            return Result<InventoryMovementGroup>.Failure(errors);
        }

        MovementGroupId groupId = MovementGroupId.New();
        (LocationId? sourceLocation, InventoryState? sourceState) = DescribeSide(spec.Legs, increase: false);
        (LocationId? destinationLocation, InventoryState? destinationState) = DescribeSide(spec.Legs, increase: true);

        List<InventoryMovement> movements = new(spec.Legs.Count);

        for (int i = 0; i < spec.Legs.Count; i++)
        {
            MovementLegSpec leg = spec.Legs[i];

            movements.Add(new InventoryMovement(
                id: InventoryMovementId.New(),
                eventId: spec.EventId,
                movementGroupId: groupId,
                legNumber: (short)(i + 1),
                productId: leg.ProductId,
                batchId: leg.BatchId,
                locationId: leg.LocationId,
                state: leg.State,
                quantityDelta: decimal.Round(leg.QuantityDelta, Quantity.Scale, MidpointRounding.ToEven),
                unitCost: decimal.Round(leg.UnitCost, Money.StorageScale, Money.IntermediateRounding),
                totalValueDelta: RoundValue(leg),
                movementType: spec.MovementType,
                sourceLocationId: sourceLocation,
                destinationLocationId: destinationLocation,
                sourceState: sourceState,
                destinationState: destinationState,
                referenceDocumentType: spec.ReferenceDocumentType,
                referenceDocumentId: spec.ReferenceDocumentId,
                referenceNumber: spec.ReferenceNumber,
                createdByUserId: spec.Actor.CreatedBy,
                approvedByUserId: spec.Actor.ApprovedBy,
                deviceId: spec.Actor.Device,
                occurredAtUtc: spec.OccurredAtUtc,
                recordedAtUtc: recordedAtUtc,
                businessDate: spec.BusinessDate,
                reasonCode: spec.ReasonCode,
                notes: spec.Notes,
                reversesMovementGroupId: spec.ReversesMovementGroupId,
                syncStatus: spec.SyncStatus,
                serverProcessingStatus: spec.ServerProcessingStatus,
                correlationId: spec.Actor.Correlation));
        }

        return Result<InventoryMovementGroup>.Success(new InventoryMovementGroup(groupId, spec, movements));
    }

    private static void ValidateLegs(MovementGroupSpec spec, MovementTypeRule rule, List<Error> errors)
    {
        if (spec.Legs.Count < 2)
        {
            errors.Add(InventoryErrors.TooFewLegs);
            return;
        }

        foreach (IGrouping<ProductId, MovementLegSpec> byProduct in spec.Legs.GroupBy(l => l.ProductId))
        {
            if (byProduct.Select(l => l.ProductTracksBatches).Distinct().Count() > 1)
            {
                errors.Add(InventoryErrors.BatchTrackingInconsistent(byProduct.Key));
            }
        }

        foreach (MovementLegSpec leg in spec.Legs)
        {
            if (leg.QuantityDelta == 0m)
            {
                errors.Add(InventoryErrors.ZeroQuantityLeg);
            }

            if (leg.UnitCost < 0m)
            {
                errors.Add(InventoryErrors.NegativeUnitCost);
            }

            if (leg.ProductTracksBatches && leg.BatchId is null)
            {
                errors.Add(InventoryErrors.BatchRequired(leg.ProductId));
            }

            if (!leg.ProductTracksBatches && leg.BatchId is not null)
            {
                errors.Add(InventoryErrors.BatchNotAllowed(leg.ProductId));
            }

            if (!MovementTypeRules.IsStateValidForLocation(leg.LocationKind, leg.State))
            {
                errors.Add(InventoryErrors.StateInvalidForLocation(leg.State));
            }

            if (leg.QuantityDelta < 0m && !rule.AllowedSourceStates.Contains(leg.State))
            {
                errors.Add(InventoryErrors.SourceStateNotAllowed(spec.MovementType, leg.State));
            }

            if (leg.QuantityDelta > 0m && !rule.AllowedDestinationStates.Contains(leg.State))
            {
                errors.Add(InventoryErrors.DestinationStateNotAllowed(spec.MovementType, leg.State));
            }
        }
    }

    private static void ValidateBalance(MovementGroupSpec spec, List<Error> errors)
    {
        // Zero-sum is checked per product AND batch: a group may not net a
        // shortage of one item against a surplus of another.
        IEnumerable<IGrouping<(ProductId Product, BatchId? Batch), MovementLegSpec>> buckets =
            spec.Legs.GroupBy(l => (Product: l.ProductId, Batch: l.BatchId));

        foreach (IGrouping<(ProductId Product, BatchId? Batch), MovementLegSpec> bucket in buckets)
        {
            decimal net = decimal.Round(
                bucket.Sum(l => l.QuantityDelta),
                Quantity.Scale,
                MidpointRounding.ToEven);

            if (net != 0m)
            {
                errors.Add(InventoryErrors.NotBalanced(bucket.Key.Product, bucket.Key.Batch, net));
            }
        }
    }

    private static void ValidateJustification(MovementGroupSpec spec, MovementTypeRule rule, List<Error> errors)
    {
        if (rule.RequiresApprover && spec.Actor.ApprovedBy is null)
        {
            errors.Add(InventoryErrors.ApproverRequired);
        }

        if (rule.RequiresReasonCode && spec.ReasonCode is null)
        {
            errors.Add(InventoryErrors.ReasonCodeRequired);
        }

        if (spec.ReasonCode == AdjustmentReasonCode.Other
            && (spec.Notes is null || spec.Notes.Trim().Length < MinimumNotesLengthForOtherReason))
        {
            errors.Add(InventoryErrors.ReasonNotesRequired);
        }

        if (spec.MovementType == InventoryMovementType.Reversal && spec.ReversesMovementGroupId is null)
        {
            errors.Add(InventoryErrors.ReversalMissingOriginal);
        }

        if (rule.RequiredReferenceDocuments is { Count: > 0 } required)
        {
            bool typeMatches = spec.ReferenceDocumentType is { } docType && required.Contains(docType);
            bool needsIdentity = required.Any(doc => doc != ReferenceDocumentType.None);

            if (!typeMatches || (needsIdentity && spec.ReferenceDocumentId is null))
            {
                errors.Add(InventoryErrors.ReferenceDocumentRequired);
            }
        }

        if (spec.MovementType != InventoryMovementType.OpeningBalance
            && string.IsNullOrWhiteSpace(spec.ReferenceNumber))
        {
            errors.Add(InventoryErrors.ReferenceDocumentRequired);
        }
    }

    private static decimal RoundValue(MovementLegSpec leg)
    {
        decimal quantity = decimal.Round(leg.QuantityDelta, Quantity.Scale, MidpointRounding.ToEven);
        decimal cost = decimal.Round(leg.UnitCost, Money.StorageScale, Money.IntermediateRounding);
        return decimal.Round(cost * quantity, Money.StorageScale, Money.IntermediateRounding);
    }

    private static (LocationId? Location, InventoryState? State) DescribeSide(
        IReadOnlyList<MovementLegSpec> legs,
        bool increase)
    {
        MovementLegSpec[] side = [.. legs.Where(l => increase ? l.QuantityDelta > 0m : l.QuantityDelta < 0m)];

        if (side.Length == 0)
        {
            return (null, null);
        }

        LocationId? location = side.Select(l => l.LocationId).Distinct().Count() == 1 ? side[0].LocationId : null;
        InventoryState? state = side.Select(l => l.State).Distinct().Count() == 1 ? side[0].State : null;

        return (location, state);
    }
}
