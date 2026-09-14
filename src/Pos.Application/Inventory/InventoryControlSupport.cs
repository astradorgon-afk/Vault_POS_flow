using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;

namespace Pos.Application.Inventory;

/// <summary>A location ready to post against: its kind, business date and write-off counterparty.</summary>
/// <param name="LocationId">The location.</param>
/// <param name="Kind">The location kind.</param>
/// <param name="TimeZoneId">The time zone.</param>
/// <param name="WriteOffLocationId">The EXT-WRITEOFF counterparty.</param>
internal sealed record PostingLocation(LocationId LocationId, LocationKind Kind, string TimeZoneId, LocationId WriteOffLocationId);

/// <summary>One bucket change to post.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="BatchId">The batch, if any.</param>
/// <param name="State">The bucket's state.</param>
/// <param name="QuantityDelta">The signed change.</param>
/// <param name="UnitCost">The valuation cost per unit.</param>
internal sealed record BucketChange(ProductId ProductId, BatchId? BatchId, InventoryState State, decimal QuantityDelta, decimal UnitCost);

/// <summary>Steps shared by the stock adjustment and inventory count handlers.</summary>
internal static class InventoryControlSupport
{
    /// <summary>How far back a count looks for an earlier variance on the same product.</summary>
    internal static readonly TimeSpan RepeatVarianceWindow = TimeSpan.FromDays(90);

    /// <summary>
    /// The cost a bucket change is valued at: the bucket's weighted average when it
    /// holds stock at a cost, otherwise the batch's cost, otherwise the product's
    /// default purchase cost.
    /// </summary>
    internal static decimal UnitCost(BucketSnapshot? bucket, Batch? batch, Product product)
        => bucket is { AverageUnitCost: > 0m } ? bucket.AverageUnitCost
            : batch is { UnitCost: > 0m } ? batch.UnitCost
            : product.DefaultPurchaseCost;

    /// <summary>Loads a stocking location with what posting needs, or the reason it cannot post.</summary>
    internal static async Task<Result<PostingLocation>> PostingLocationAsync(
        IInventoryControlRepository repository,
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        InventoryControlLocation? location = await repository
            .GetLocationAsync(locationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null || location.Kind == LocationKind.External)
        {
            return Result<PostingLocation>.Failure(InventoryControlErrors.LocationInvalid(locationId));
        }

        if (string.IsNullOrWhiteSpace(location.TimeZoneId))
        {
            return Result<PostingLocation>.Failure(InventoryControlErrors.LocationTimeZoneMissing(locationId));
        }

        LocationId? writeOff = await repository
            .GetExternalWriteOffLocationIdAsync(cancellationToken)
            .ConfigureAwait(false);

        return writeOff is null
            ? Result<PostingLocation>.Failure(InventoryControlErrors.WriteOffLocationMissing)
            : Result<PostingLocation>.Success(new PostingLocation(locationId, location.Kind, location.TimeZoneId, writeOff.Value));
    }

    /// <summary>
    /// Builds the balanced legs for bucket changes: an expiry move stays inside the
    /// location (Available to Expired); everything else balances against EXT-WRITEOFF,
    /// which gives up stock a change adds and receives stock a change removes.
    /// </summary>
    internal static List<MovementLegSpec> Legs(
        InventoryMovementType movementType,
        IEnumerable<BucketChange> changes,
        PostingLocation location,
        IReadOnlyDictionary<ProductId, Product> products)
    {
        List<MovementLegSpec> legs = [];

        foreach (BucketChange change in changes)
        {
            bool tracksBatches = products[change.ProductId].TracksBatches;

            legs.Add(new MovementLegSpec(
                change.ProductId, change.BatchId, location.LocationId, location.Kind, change.State,
                change.QuantityDelta, change.UnitCost, tracksBatches));

            legs.Add(movementType == InventoryMovementType.ExpiryQuarantine
                ? new MovementLegSpec(
                    change.ProductId, change.BatchId, location.LocationId, location.Kind, InventoryState.Expired,
                    -change.QuantityDelta, change.UnitCost, tracksBatches)
                : new MovementLegSpec(
                    change.ProductId, change.BatchId, location.WriteOffLocationId, LocationKind.External,
                    InventoryState.External, -change.QuantityDelta, change.UnitCost, tracksBatches));
        }

        return legs;
    }

    /// <summary>Checks the caller holds a permission at a document's location.</summary>
    internal static Task<bool> InScopeAsync(
        IPermissionEvaluator permissions,
        ICurrentUser currentUser,
        string permission,
        LocationId locationId,
        CancellationToken cancellationToken)
        => permissions.HasPermissionAsync(currentUser.UserId ?? UserId.Empty, permission, locationId, cancellationToken);

    /// <summary>Stages an audit entry for an adjustment or count.</summary>
    internal static Task AuditAsync(
        IAuditWriter audit,
        string action,
        string entityType,
        Guid entityId,
        LocationId locationId,
        object? details,
        string? reason,
        ReferenceDocumentType documentType,
        CancellationToken cancellationToken)
        => audit.WriteAsync(
            new AuditEntry(
                action,
                entityType,
                entityId,
                NewValueJson: details is null ? null : JsonSerializer.Serialize(details),
                Reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                ReferenceDocumentType: documentType,
                ReferenceDocumentId: entityId,
                LocationId: locationId),
            cancellationToken);

    /// <summary>Loads the products named on a document, or the first one that does not exist.</summary>
    internal static async Task<Result<Dictionary<ProductId, Product>>> ProductsAsync(
        IInventoryControlRepository repository,
        IEnumerable<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        ProductId[] ids = [.. productIds.Distinct()];

        Dictionary<ProductId, Product> products = (await repository
                .GetProductsAsync(ids, cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(p => p.Id);

        foreach (ProductId id in ids)
        {
            if (!products.ContainsKey(id))
            {
                return Result<Dictionary<ProductId, Product>>.Failure(InventoryControlErrors.ProductUnknown(id));
            }
        }

        return Result<Dictionary<ProductId, Product>>.Success(products);
    }

    /// <summary>Loads the batches named on a document and checks each belongs to its line's product.</summary>
    internal static async Task<Result<Dictionary<BatchId, Batch>>> BatchesAsync(
        IInventoryControlRepository repository,
        IReadOnlyList<(ProductId ProductId, BatchId? BatchId)> lines,
        CancellationToken cancellationToken)
    {
        BatchId[] ids = [.. lines.Where(l => l.BatchId is { IsEmpty: false }).Select(l => l.BatchId!.Value).Distinct()];

        Dictionary<BatchId, Batch> batches = (await repository
                .GetBatchesAsync(ids, cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(b => b.Id);

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].BatchId is { IsEmpty: false } batchId
                && (!batches.TryGetValue(batchId, out Batch? batch) || batch.ProductId != lines[i].ProductId))
            {
                return Result<Dictionary<BatchId, Batch>>.Failure(InventoryControlErrors.BatchMismatch(i + 1));
            }
        }

        return Result<Dictionary<BatchId, Batch>>.Success(batches);
    }
}
