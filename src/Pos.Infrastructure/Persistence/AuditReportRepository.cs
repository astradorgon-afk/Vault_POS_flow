using Microsoft.EntityFrameworkCore;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Quarantine;
using Pos.Domain.Reports;
using Pos.Application.Reports;

namespace Pos.Infrastructure.Persistence;

/// <inheritdoc cref="IAuditReportRepository" />
/// <remarks>
/// <para>
/// The activity report deliberately omits the before-and-after payloads. They can
/// carry customer details and prices, and an activity list is read far more often
/// than a single entry is examined; whoever needs the payload opens the entry.
/// </para>
/// <para>
/// An audit entry with no location is business-wide — a role change, a permission
/// grant — and is visible only to a caller who is not scoped to particular stores.
/// Showing it to a store manager would leak head-office activity through a report
/// about their own shop; hiding it from an owner would lose the entries that
/// matter most.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class AuditReportRepository(PosDbContext context) : IAuditReportRepository
{
    /// <inheritdoc />
    public async Task<AuditActivityReport> GetActivityAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        UserId? userId,
        string? action,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<AuditLogEntry> entries = context.AuditLog
            .AsNoTracking()
            .Where(e => e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc <= toUtc);

        if (locations.Count > 0)
        {
            // Scoped callers see their own stores and nothing business-wide.
            entries = entries.Where(e => e.LocationId != null && locations.Contains(e.LocationId.Value));
        }

        if (userId is { } actor)
        {
            entries = entries.Where(e => e.UserId == actor);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            entries = entries.Where(e => e.Action == action);
        }

        // One more than asked for, so "there is more behind this" is something the
        // query answered rather than a guess from a suspiciously round count.
        List<AuditLogEntry> page = await entries
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        bool truncated = page.Count > limit;
        List<AuditLogEntry> rows = [.. page.Take(limit)];

        Dictionary<Guid, string> codes = await CodesAsync(
            [.. rows.Where(e => e.LocationId is not null).Select(e => e.LocationId!.Value).Distinct()],
            cancellationToken).ConfigureAwait(false);

        return new AuditActivityReport(
            fromUtc,
            toUtc,
            [
                .. rows.Select(e => new AuditActivityRow(
                    e.OccurredAtUtc,
                    e.Action,
                    e.EntityType,
                    e.EntityId,
                    e.UserId?.Value,
                    e.UserRoleSnapshot,
                    e.DeviceId?.Value,
                    e.LocationId?.Value,
                    e.LocationId is { } at && codes.TryGetValue(at.Value, out string? code) ? code : string.Empty,
                    e.Reason,
                    e.ReferenceDocumentId,
                    e.CorrelationId.Value)),
            ],
            truncated);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuarantineIncidentRow>> GetQuarantineIncidentsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        bool openOnly,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        IQueryable<QuarantineIncident> incidents = context.QuarantineIncidents
            .AsNoTracking()
            .Include(i => i.Lines)
            .Where(i => i.CreatedAtUtc >= fromUtc && i.CreatedAtUtc <= toUtc);

        if (locations.Count > 0)
        {
            incidents = incidents.Where(i => locations.Contains(i.LocationId));
        }

        List<QuarantineIncident> raised = await incidents
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, string> codes = await CodesAsync(
            [.. raised.Select(i => i.LocationId).Distinct()], cancellationToken).ConfigureAwait(false);

        return
        [
            .. raised
                .Where(i => !openOnly || i.ResolvedAtUtc is null)
                .Select(i => new QuarantineIncidentRow(
                    i.Id.Value,
                    i.Number,
                    i.Status,
                    i.LocationId.Value,
                    codes.TryGetValue(i.LocationId.Value, out string? code) ? code : string.Empty,
                    i.CreatedAtUtc,
                    i.ResolvedAtUtc,

                    // Open incidents age to now; closed ones keep how long they
                    // took. One number answers both "how long has this been
                    // sitting" and "how long did that one take".
                    (int)Math.Floor(((i.ResolvedAtUtc ?? asOfUtc) - i.CreatedAtUtc).TotalDays),
                    i.Lines.Count,
                    i.Lines.Sum(l => l.Quantity),
                    i.Lines.Count(l => l.ProductId is null)))
                .OrderByDescending(r => r.ResolvedAtUtc is null)
                .ThenByDescending(r => r.DaysOpen)
                .ThenBy(r => r.Number, StringComparer.Ordinal),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiringStockRow>> GetExpiringStockAsync(
        DateOnly asOf,
        int withinDays,
        IReadOnlyCollection<LocationId> locations,
        int limit,
        CancellationToken cancellationToken)
    {
        DateOnly horizon = asOf.AddDays(withinDays);

        IQueryable<InventoryBalance> balances = context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.Quantity > 0m && b.BatchKey != BatchId.Empty);

        if (locations.Count > 0)
        {
            balances = balances.Where(b => locations.Contains(b.LocationId));
        }

        var rows = await (
            from balance in balances
            join batch in context.Batches.AsNoTracking() on balance.BatchKey equals batch.Id
            join product in context.Products.AsNoTracking() on balance.ProductId equals product.Id
            join location in context.Locations.AsNoTracking() on balance.LocationId equals location.Id

            // Everything already past its date is included however far back it
            // went: a batch that expired last month is more urgent than one
            // expiring next week, not less.
            where batch.ExpiresOn != null && batch.ExpiresOn <= horizon
            select new
            {
                balance.ProductId,
                product.Sku,
                ProductName = product.Name,
                balance.LocationId,
                location.Code,
                BatchId = batch.Id,
                batch.LotNumber,
                ExpiresOn = batch.ExpiresOn!.Value,
                balance.State,
                balance.Quantity,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows
                .Select(r => new ExpiringStockRow(
                    r.ProductId.Value,
                    r.Sku.Value,
                    r.ProductName,
                    r.LocationId.Value,
                    r.Code,
                    r.BatchId.Value,
                    r.LotNumber,
                    r.ExpiresOn,
                    r.ExpiresOn.DayNumber - asOf.DayNumber,
                    r.State,
                    r.Quantity))
                .OrderBy(r => r.ExpiresOn)
                .ThenByDescending(r => r.Quantity)
                .ThenBy(r => r.Sku, StringComparer.Ordinal)
                .Take(limit),
        ];
    }

    private async Task<Dictionary<Guid, string>> CodesAsync(
        List<LocationId> ids,
        CancellationToken cancellationToken)
    {
        var rows = await context.Locations
            .AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new { Id = l.Id.Value, l.Code })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => r.Code);
    }
}
