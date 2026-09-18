using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Inventory;

/// <summary>Reads an authorization-scoped movement chain for one document.</summary>
public sealed class InventoryTimelineService(
    PosDbContext context,
    ICurrentUser currentUser)
{
    public async Task<InventoryTimeline?> GetAsync(
        ReferenceDocumentType documentType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        IQueryable<InventoryMovement> query = context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ReferenceDocumentType == documentType
                        && m.ReferenceDocumentId == documentId);

        if (!currentUser.HasAllLocations)
        {
            LocationId[] assigned = [.. currentUser.AssignedLocations];
            query = query.Where(m => assigned.Contains(m.LocationId));
        }

        List<InventoryMovement> movements = await query
            .OrderBy(m => m.RecordedAtUtc)
            .ThenBy(m => m.MovementGroupId)
            .ThenBy(m => m.LegNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (movements.Count == 0)
        {
            return null;
        }

        Guid[] productIds = [.. movements.Select(m => m.ProductId.Value).Distinct()];
        Dictionary<Guid, string> productNames = await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id.Value))
            .ToDictionaryAsync(p => p.Id.Value, p => p.Name, cancellationToken)
            .ConfigureAwait(false);

        List<InventoryTimelineGroup> groups = [.. movements
            .GroupBy(m => m.MovementGroupId)
            .Select(group => new InventoryTimelineGroup(
                group.Key.Value,
                group.First().MovementType.ToString(),
                group.First().ReferenceNumber,
                group.First().OccurredAtUtc,
                group.First().RecordedAtUtc,
                group.Select(m => new InventoryTimelineLeg(
                    m.Id.Value,
                    m.LegNumber,
                    m.ProductId.Value,
                    productNames.GetValueOrDefault(m.ProductId.Value, "Unknown product"),
                    m.LocationId.Value,
                    m.State.ToString(),
                    m.QuantityDelta,
                    m.UnitCost,
                    m.SourceLocationId?.Value,
                    m.DestinationLocationId?.Value,
                    m.ReversesMovementGroupId?.Value)).ToList()))];

        return new InventoryTimeline(
            documentType.ToString(),
            documentId,
            movements[0].ReferenceNumber,
            groups);
    }
}

public sealed record InventoryTimeline(
    string DocumentType,
    Guid DocumentId,
    string ReferenceNumber,
    IReadOnlyList<InventoryTimelineGroup> Groups);

public sealed record InventoryTimelineGroup(
    Guid MovementGroupId,
    string MovementType,
    string ReferenceNumber,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    IReadOnlyList<InventoryTimelineLeg> Legs);

public sealed record InventoryTimelineLeg(
    Guid MovementId,
    short LegNumber,
    Guid ProductId,
    string ProductName,
    Guid LocationId,
    string State,
    decimal QuantityDelta,
    decimal UnitCost,
    Guid? SourceLocationId,
    Guid? DestinationLocationId,
    Guid? ReversesMovementGroupId);
