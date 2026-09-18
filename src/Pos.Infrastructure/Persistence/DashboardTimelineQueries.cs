using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Reports;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// The drill-down half of the owner dashboard: what happened to one document.
/// </summary>
/// <remarks>
/// <para>
/// Three sources are merged — the audit log, the ledger and the sync verdicts —
/// and every entry says which it came from. A person chasing a discrepancy needs
/// to know whether they are looking at something somebody did, something the
/// ledger posted, or something head office decided about an upload, and a merged
/// list without the label invites reading one as another.
/// </para>
/// <para>
/// Ordered by when the system recorded each thing rather than by when it happened.
/// An offline sale uploaded on Tuesday occurred on Monday, and a timeline sorted
/// by occurrence would put its ledger posting before the shift that contained it.
/// Both times are carried, so a reader sees the gap rather than being protected
/// from it.
/// </para>
/// </remarks>
public sealed partial class DashboardRepository
{
    /// <inheritdoc />
    public async Task<DocumentTimeline?> GetTimelineAsync(
        ReferenceDocumentType referenceType,
        Guid referenceId,
        IReadOnlyCollection<LocationId> locations,
        bool includeFinancial,
        CancellationToken cancellationToken)
    {
        List<InventoryMovement> legs = await context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ReferenceDocumentType == referenceType && m.ReferenceDocumentId == referenceId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AuditLogEntryRow> audit = await AuditAsync(referenceType, referenceId, cancellationToken)
            .ConfigureAwait(false);

        // A document nothing in scope touched is refused rather than shown empty:
        // "you may not see this" and "nothing happened" are different answers, and
        // an empty timeline would quietly tell a manager the second.
        if (locations.Count > 0)
        {
            bool touchesScope = legs.Exists(m => locations.Contains(m.LocationId))
                || audit.Exists(a => a.LocationId is { } at && locations.Contains(at));

            if (!touchesScope)
            {
                return null;
            }
        }

        if (legs.Count == 0 && audit.Count == 0)
        {
            return null;
        }

        Dictionary<Guid, (string Sku, string Name)> products = await ProductsAsync(legs, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, string> codes = await CodeMapAsync(
            [.. legs.Select(m => m.LocationId).Distinct()], cancellationToken).ConfigureAwait(false);

        List<MovementGroupView> movements = Chain(legs, products, codes, includeFinancial);

        List<DocumentTimelineEntry> entries =
        [
            .. audit.Select(a => new DocumentTimelineEntry(
                TimelineSource.Audit,
                a.OccurredAtUtc,
                null,
                a.Action,
                a.Reason ?? a.EntityType,
                a.UserId,
                a.UserRoleSnapshot,
                a.DeviceId,
                a.LocationId?.Value,
                a.CorrelationId)),

            .. movements.Select(m => new DocumentTimelineEntry(
                TimelineSource.Ledger,
                m.RecordedAtUtc,
                m.OccurredAtUtc,
                m.MovementType.ToString(),
                FormattableString.Invariant(
                    $"{m.Legs.Count} legs, net {m.Legs.Sum(l => l.QuantityDelta)}"),
                null,
                null,
                null,
                m.Legs.Count == 0 ? null : m.Legs[0].LocationId,
                null)),

            .. await VerdictsAsync(legs, cancellationToken).ConfigureAwait(false),
        ];

        return new DocumentTimeline(
            referenceType,
            referenceId,
            legs.Count == 0 ? null : legs[0].ReferenceNumber,
            [.. entries.OrderBy(e => e.AtUtc).ThenBy(e => e.Source)],
            movements);
    }

    /// <summary>
    /// Groups the legs into postings and links each reversal to what it reversed.
    /// </summary>
    /// <remarks>
    /// The link is followed in both directions. A reversal that only pointed
    /// backwards would leave somebody reading the original posting with no sign it
    /// had been undone, which is exactly the reading that leads to counting the
    /// same loss twice.
    /// </remarks>
    private static List<MovementGroupView> Chain(
        List<InventoryMovement> legs,
        Dictionary<Guid, (string Sku, string Name)> products,
        Dictionary<Guid, string> codes,
        bool includeFinancial)
    {
        Dictionary<Guid, Guid> reversedBy = legs
            .Where(m => m.ReversesMovementGroupId is not null)
            .GroupBy(m => m.ReversesMovementGroupId!.Value.Value)
            .ToDictionary(g => g.Key, g => g.First().MovementGroupId.Value);

        return
        [
            .. legs
                .GroupBy(m => m.MovementGroupId)
                .Select(group =>
                {
                    InventoryMovement first = group.First();

                    return new MovementGroupView(
                        group.Key.Value,
                        first.EventId.Value,
                        first.MovementType,
                        first.RecordedAtUtc,
                        first.OccurredAtUtc,
                        first.ReversesMovementGroupId?.Value,
                        reversedBy.TryGetValue(group.Key.Value, out Guid undoneBy) ? undoneBy : null,
                        [
                            .. group
                                .OrderBy(m => m.LegNumber)
                                .Select(m => new MovementLegView(
                                    m.ProductId.Value,
                                    products.TryGetValue(m.ProductId.Value, out (string Sku, string Name) p)
                                        ? p.Sku
                                        : string.Empty,
                                    m.LocationId.Value,
                                    codes.TryGetValue(m.LocationId.Value, out string? code) ? code : string.Empty,
                                    m.State,
                                    m.BatchId?.Value,
                                    m.QuantityDelta,
                                    includeFinancial ? m.TotalValueDelta : null)),
                        ]);
                })
                .OrderBy(m => m.RecordedAtUtc),
        ];
    }

    /// <summary>
    /// What head office decided about the events these postings were made under.
    /// </summary>
    /// <remarks>
    /// Only present for a document a register uploaded. An online sale has no sync
    /// verdict and its timeline simply has no such lines, rather than lines saying
    /// nothing happened.
    /// </remarks>
    private async Task<List<DocumentTimelineEntry>> VerdictsAsync(
        List<InventoryMovement> legs,
        CancellationToken cancellationToken)
    {
        List<EventId> eventIds = [.. legs.Select(m => m.EventId).Distinct()];

        if (eventIds.Count == 0)
        {
            return [];
        }

        var rows = await context.ProcessedEvents
            .AsNoTracking()
            .Where(e => eventIds.Contains(e.EventId))
            .Select(e => new { e.EventId, e.DeviceId, e.Type, e.Outcome, e.ResultJson, e.AppliedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new DocumentTimelineEntry(
                TimelineSource.Sync,
                r.AppliedAtUtc,
                null,
                r.Outcome,
                r.ResultJson is { Length: > 0 } detail
                    ? FormattableString.Invariant($"{r.Type}: {detail}")
                    : r.Type,
                null,
                null,
                r.DeviceId.Value,
                null,
                null)),
        ];
    }

    private async Task<List<AuditLogEntryRow>> AuditAsync(
        ReferenceDocumentType referenceType,
        Guid referenceId,
        CancellationToken cancellationToken)
    {
        var rows = await context.AuditLog
            .AsNoTracking()
            .Where(e => (e.ReferenceDocumentType == referenceType && e.ReferenceDocumentId == referenceId)
                        || e.EntityId == referenceId)
            .Select(e => new
            {
                e.Action,
                e.EntityType,
                e.UserId,
                e.UserRoleSnapshot,
                e.DeviceId,
                e.LocationId,
                e.Reason,
                e.OccurredAtUtc,
                e.CorrelationId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new AuditLogEntryRow(
                r.Action,
                r.EntityType,
                r.UserId?.Value,
                r.UserRoleSnapshot,
                r.DeviceId?.Value,
                r.LocationId,
                r.Reason,
                r.OccurredAtUtc,
                r.CorrelationId.Value)),
        ];
    }

    private async Task<Dictionary<Guid, (string Sku, string Name)>> ProductsAsync(
        List<InventoryMovement> legs,
        CancellationToken cancellationToken)
    {
        List<ProductId> ids = [.. legs.Select(m => m.ProductId).Distinct()];

        var rows = await context.Products
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { Id = p.Id.Value, p.Sku, p.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => (r.Sku.Value, r.Name));
    }

    private sealed record AuditLogEntryRow(
        string Action,
        string EntityType,
        Guid? UserId,
        string? UserRoleSnapshot,
        Guid? DeviceId,
        LocationId? LocationId,
        string? Reason,
        DateTimeOffset OccurredAtUtc,
        Guid CorrelationId);
}
