using Microsoft.EntityFrameworkCore;
using Pos.Application.Reports;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Inventory;
using Pos.Domain.Notifications;
using Pos.Domain.Quarantine;
using Pos.Domain.Reports;
using Pos.Domain.Transfers;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// The exception half of the owner dashboard.
/// </summary>
/// <remarks>
/// <para>
/// Each panel is its own small query. One query joining nine things would be
/// shorter to write and impossible to read, and the panels genuinely answer
/// different questions: some are a standing state — what is unresolved right now —
/// and some count what happened during the period.
/// </para>
/// <para>
/// Every panel reports its <b>whole</b> count and a handful of examples. A panel
/// that said "5" because it had only looked at five would be the worst kind of
/// wrong, because it would read as good news.
/// </para>
/// </remarks>
public sealed partial class DashboardRepository
{
    /// <inheritdoc />
    public async Task<DashboardExceptions> GetExceptionsAsync(
        DashboardQuery query,
        decimal highValueThreshold,
        TimeSpan offlineAfter,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset windowStart = new(query.FromDate, TimeOnly.MinValue, TimeSpan.Zero);
        DateTimeOffset windowEnd = new(query.ToDate.AddDays(1), TimeOnly.MinValue, TimeSpan.Zero);

        List<DashboardExceptionPanel> panels =
        [
            await ExpiredStillSellableAsync(query, sampleSize, cancellationToken).ConfigureAwait(false),
            await UnknownProductsAsync(query, sampleSize, cancellationToken).ConfigureAwait(false),
            await NegativeStockAsync(query, windowStart, windowEnd, sampleSize, cancellationToken).ConfigureAwait(false),
            await FailedSyncAsync(query, sampleSize, cancellationToken).ConfigureAwait(false),
            await EmergencyTransfersAsync(query, sampleSize, cancellationToken).ConfigureAwait(false),
            await DiscrepanciesAsync(query, sampleSize, cancellationToken).ConfigureAwait(false),
            await HighValueAdjustmentsAsync(query, windowStart, windowEnd, highValueThreshold, sampleSize, cancellationToken)
                .ConfigureAwait(false),
            await RepeatedVariancesAsync(query, windowStart, windowEnd, sampleSize, cancellationToken).ConfigureAwait(false),
            await OfflineDevicesAsync(query, now, offlineAfter, sampleSize, cancellationToken).ConfigureAwait(false),
        ];

        return new DashboardExceptions(
            query.FromDate,
            query.ToDate,
            now,

            // Worst first, then most numerous, then oldest. An empty panel is kept:
            // a board that hid its clean rows would leave a reader unsure whether
            // there was nothing wrong or nothing looked at.
            [
                .. panels
                    .OrderByDescending(p => p.Count == 0 ? -1 : (int)p.Severity)
                    .ThenByDescending(p => p.Count)
                    .ThenBy(p => p.OldestAtUtc ?? DateTimeOffset.MaxValue),
            ]);
    }

    /// <summary>
    /// Stock past its expiry date sitting where a till may sell it.
    /// </summary>
    /// <remarks>
    /// The most serious panel on the board, and the reason it is first: every other
    /// exception is money or paperwork, and this one can reach a customer. Expiry
    /// is a date rather than an instant, so a batch is past it once the business
    /// day has moved on.
    /// </remarks>
    private async Task<DashboardExceptionPanel> ExpiredStillSellableAsync(
        DashboardQuery query,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        DateOnly today = clock.BusinessDateFor(query.TimeZoneId);

        var rows = await (
            from balance in Scoped(
                context.InventoryBalances.AsNoTracking()
                    .Where(b => b.Quantity > 0m && b.State == InventoryState.Available),
                query,
                b => query.Locations.Contains(b.LocationId))
            join batch in context.Batches.AsNoTracking() on balance.BatchKey equals batch.Id
            join product in context.Products.AsNoTracking() on balance.ProductId equals product.Id
            join location in context.Locations.AsNoTracking() on balance.LocationId equals location.Id
            where batch.ExpiresOn != null && batch.ExpiresOn < today
            select new
            {
                BatchId = batch.Id.Value,
                batch.LotNumber,
                batch.ExpiresOn,
                product.Sku,
                location.Code,
                balance.Quantity,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new DashboardExceptionPanel(
            DashboardExceptionKind.ExpiredStillSellable,
            NotificationSeverity.Critical,
            rows.Count,
            rows.Count == 0
                ? null
                : new DateTimeOffset(rows.Min(r => r.ExpiresOn!.Value), TimeOnly.MinValue, TimeSpan.Zero),
            null,
            [
                .. rows
                    .OrderBy(r => r.ExpiresOn)
                    .Take(sampleSize)
                    .Select(r => new DashboardExceptionItem(
                        r.BatchId,
                        r.LotNumber,
                        r.Code,
                        new DateTimeOffset(r.ExpiresOn!.Value, TimeOnly.MinValue, TimeSpan.Zero),
                        FormattableString.Invariant(
                            $"{r.Sku.Value}: {r.Quantity} sellable, expired {r.ExpiresOn:yyyy-MM-dd}"))),
            ]);
    }

    private async Task<DashboardExceptionPanel> UnknownProductsAsync(
        DashboardQuery query,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        List<QuarantineIncident> open = await Scoped(
            context.QuarantineIncidents.AsNoTracking().Include(i => i.Lines).Where(i => i.ResolvedAtUtc == null),
            query,
            i => query.Locations.Contains(i.LocationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, string> codes = await CodeMapAsync(
            [.. open.Select(i => i.LocationId).Distinct()], cancellationToken).ConfigureAwait(false);

        var unknown = open
            .SelectMany(i => i.Lines.Where(l => l.ProductId is null).Select(l => new { Incident = i, Line = l }))
            .ToList();

        return new DashboardExceptionPanel(
            DashboardExceptionKind.UnknownProducts,
            NotificationSeverity.Warning,
            unknown.Count,
            unknown.Count == 0 ? null : unknown.Min(u => u.Incident.CreatedAtUtc),
            null,
            [
                .. unknown
                    .OrderBy(u => u.Incident.CreatedAtUtc)
                    .Take(sampleSize)
                    .Select(u => new DashboardExceptionItem(
                        u.Incident.Id.Value,
                        u.Incident.Number,
                        Code(codes, u.Incident.LocationId),
                        u.Incident.CreatedAtUtc,
                        FormattableString.Invariant(
                            $"Barcode {u.Line.Barcode}, {u.Line.Quantity} units, claimed as {u.Line.ClaimedProductName ?? "nothing"}"))),
            ]);
    }

    private async Task<DashboardExceptionPanel> NegativeStockAsync(
        DashboardQuery query,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        List<NegativeStockAttempt> attempts = await Scoped(
            context.NegativeStockAttempts.AsNoTracking()
                .Where(a => a.AttemptedAtUtc >= windowStart && a.AttemptedAtUtc < windowEnd),
            query,
            a => query.Locations.Contains(a.LocationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, string> codes = await CodeMapAsync(
            [.. attempts.Select(a => a.LocationId).Distinct()], cancellationToken).ConfigureAwait(false);

        return new DashboardExceptionPanel(
            DashboardExceptionKind.NegativeStockAttempts,
            NotificationSeverity.Critical,
            attempts.Count,
            attempts.Count == 0 ? null : attempts.Min(a => a.AttemptedAtUtc),
            null,
            [
                .. attempts
                    .OrderByDescending(a => a.AttemptedAtUtc)
                    .Take(sampleSize)
                    .Select(a => new DashboardExceptionItem(
                        a.EventId.Value,
                        a.ReferenceNumber,
                        Code(codes, a.LocationId),
                        a.AttemptedAtUtc,

                        // The policy is carried because a permitted oversell and a
                        // refused one look identical without it, and they are
                        // opposite facts: one shelf is now wrong, the other is not.
                        FormattableString.Invariant(
                            $"Wanted {a.RequestedQuantity}, had {a.AvailableQuantity} ({a.Policy})"))),
            ]);
    }

    private async Task<DashboardExceptionPanel> FailedSyncAsync(
        DashboardQuery query,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        string[] unresolved =
        [
            nameof(SyncOutcome.Rejected), nameof(SyncOutcome.RequiresReview), nameof(SyncOutcome.Conflict),
        ];

        var rows = await (
            from processed in context.ProcessedEvents.AsNoTracking()
            join device in context.Set<Device>().AsNoTracking() on processed.DeviceId equals device.Id
            join location in context.Locations.AsNoTracking() on device.LocationId equals location.Id
            where unresolved.Contains(processed.Outcome)
                  && (query.Locations.Count == 0 || query.Locations.Contains(device.LocationId))
            select new
            {
                processed.EventId,
                device.ShortCode,
                location.Code,
                processed.Type,
                processed.Outcome,
                processed.AppliedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new DashboardExceptionPanel(
            DashboardExceptionKind.FailedSync,
            NotificationSeverity.Critical,
            rows.Count,
            rows.Count == 0 ? null : rows.Min(r => r.AppliedAtUtc),
            null,
            [
                .. rows
                    .OrderBy(r => r.AppliedAtUtc)
                    .Take(sampleSize)
                    .Select(r => new DashboardExceptionItem(
                        r.EventId.Value,
                        r.ShortCode,
                        r.Code,
                        r.AppliedAtUtc,
                        FormattableString.Invariant($"{r.Type} — {r.Outcome}"))),
            ]);
    }

    private async Task<DashboardExceptionPanel> EmergencyTransfersAsync(
        DashboardQuery query,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        List<Transfer> raised = await context.Transfers
            .AsNoTracking()
            .Where(t => t.Mode == TransferMode.EmergencyOffline && t.VerifiedAtUtc == null)
            .Where(t => query.Locations.Count == 0
                        || query.Locations.Contains(t.SourceLocationId)
                        || query.Locations.Contains(t.DestinationLocationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, string> codes = await CodeMapAsync(
            [.. raised.Select(t => t.SourceLocationId).Concat(raised.Select(t => t.DestinationLocationId)).Distinct()],
            cancellationToken).ConfigureAwait(false);

        return new DashboardExceptionPanel(
            DashboardExceptionKind.EmergencyTransfers,
            NotificationSeverity.Critical,
            raised.Count,
            raised.Count == 0 ? null : raised.Min(t => t.CreatedAtUtc),
            null,
            [
                .. raised
                    .OrderBy(t => t.CreatedAtUtc)
                    .Take(sampleSize)
                    .Select(t => new DashboardExceptionItem(
                        t.Id.Value,
                        t.Number,
                        Code(codes, t.SourceLocationId),
                        t.CreatedAtUtc,
                        FormattableString.Invariant(
                            $"To {Code(codes, t.DestinationLocationId)}, {t.Status}"))),
            ]);
    }

    private async Task<DashboardExceptionPanel> DiscrepanciesAsync(
        DashboardQuery query,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        List<Transfer> transfers = await context.Transfers
            .AsNoTracking()
            .Include(t => t.Discrepancies)
            .Where(t => t.Discrepancies.Any(d => d.ResolutionOutcome == null))
            .Where(t => query.Locations.Count == 0
                        || query.Locations.Contains(t.SourceLocationId)
                        || query.Locations.Contains(t.DestinationLocationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, string> codes = await CodeMapAsync(
            [.. transfers.Select(t => t.DestinationLocationId).Distinct()], cancellationToken).ConfigureAwait(false);

        var open = transfers
            .SelectMany(t => t.Discrepancies.Where(d => d.ResolutionOutcome is null)
                .Select(d => new { Transfer = t, Discrepancy = d }))
            .ToList();

        return new DashboardExceptionPanel(
            DashboardExceptionKind.TransferDiscrepancies,
            NotificationSeverity.Warning,
            open.Count,
            open.Count == 0 ? null : open.Min(o => o.Transfer.ReceivedAtUtc ?? o.Transfer.CreatedAtUtc),
            null,
            [
                .. open
                    .OrderBy(o => o.Transfer.ReceivedAtUtc ?? o.Transfer.CreatedAtUtc)
                    .Take(sampleSize)
                    .Select(o => new DashboardExceptionItem(
                        o.Transfer.Id.Value,
                        o.Transfer.Number,
                        Code(codes, o.Transfer.DestinationLocationId),
                        o.Transfer.ReceivedAtUtc,
                        FormattableString.Invariant($"{o.Discrepancy.Kind} of {o.Discrepancy.Quantity}"))),
            ]);
    }

    private async Task<DashboardExceptionPanel> HighValueAdjustmentsAsync(
        DashboardQuery query,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        decimal threshold,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        InventoryMovementType[] adjustments =
        [
            InventoryMovementType.Damage, InventoryMovementType.Spoilage, InventoryMovementType.Loss,
            InventoryMovementType.Theft, InventoryMovementType.ExpiryWriteOff,
            InventoryMovementType.ApprovedStockAdjustment,
            InventoryMovementType.CountAdjustmentIncrease, InventoryMovementType.CountAdjustmentDecrease,
        ];

        var rows = await (
            from movement in Scoped(
                context.InventoryMovements.AsNoTracking()
                    .Where(m => m.RecordedAtUtc >= windowStart
                                && m.RecordedAtUtc < windowEnd
                                && adjustments.Contains(m.MovementType)
                                && m.State != InventoryState.External),
                query,
                m => query.Locations.Contains(m.LocationId))
            join location in context.Locations.AsNoTracking() on movement.LocationId equals location.Id
            select new
            {
                movement.EventId,
                movement.ReferenceNumber,
                location.Code,
                movement.RecordedAtUtc,
                movement.MovementType,
                movement.TotalValueDelta,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var high = rows.Where(r => Math.Abs(r.TotalValueDelta) >= threshold).ToList();

        return new DashboardExceptionPanel(
            DashboardExceptionKind.HighValueAdjustments,
            NotificationSeverity.Warning,
            high.Count,
            high.Count == 0 ? null : high.Min(r => r.RecordedAtUtc),

            // The count is operational — three adjustments crossed the line — and
            // what they came to is financial. One is shown to everybody and the
            // other only to a caller who may see money.
            query.IncludeFinancial ? high.Sum(r => Math.Abs(r.TotalValueDelta)) : null,
            [
                .. high
                    .OrderByDescending(r => Math.Abs(r.TotalValueDelta))
                    .Take(sampleSize)
                    .Select(r => new DashboardExceptionItem(
                        r.EventId.Value,
                        r.ReferenceNumber,
                        r.Code,
                        r.RecordedAtUtc,
                        query.IncludeFinancial
                            ? FormattableString.Invariant($"{r.MovementType} worth {Math.Abs(r.TotalValueDelta)}")
                            : r.MovementType.ToString())),
            ]);
    }

    private async Task<DashboardExceptionPanel> RepeatedVariancesAsync(
        DashboardQuery query,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        List<InventoryCount> counts = await Scoped(
            context.InventoryCounts.AsNoTracking().Include(c => c.Lines)
                .Where(c => c.CreatedAtUtc >= windowStart && c.CreatedAtUtc < windowEnd),
            query,
            c => query.Locations.Contains(c.LocationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, string> codes = await CodeMapAsync(
            [.. counts.Select(c => c.LocationId).Distinct()], cancellationToken).ConfigureAwait(false);

        var repeats = counts
            .SelectMany(c => c.Lines
                .Where(l => l.IsRepeatVariance && l.PhysicalQuantity != null && l.PhysicalQuantity != l.SystemQuantity)
                .Select(l => new { Count = c, Line = l }))
            .ToList();

        return new DashboardExceptionPanel(
            DashboardExceptionKind.RepeatedCountVariances,
            NotificationSeverity.Warning,
            repeats.Count,
            repeats.Count == 0 ? null : repeats.Min(r => r.Count.CreatedAtUtc),
            null,
            [
                .. repeats
                    .OrderByDescending(r => Math.Abs((r.Line.PhysicalQuantity ?? 0m) - r.Line.SystemQuantity))
                    .Take(sampleSize)
                    .Select(r => new DashboardExceptionItem(
                        r.Count.Id.Value,
                        r.Count.Number,
                        Code(codes, r.Count.LocationId),
                        r.Count.CreatedAtUtc,
                        FormattableString.Invariant(
                            $"Counted {r.Line.PhysicalQuantity}, system said {r.Line.SystemQuantity}, and it varied last time too"))),
            ]);
    }

    /// <summary>
    /// Registers that have not been heard from.
    /// </summary>
    /// <remarks>
    /// A register enrolled and never seen counts. It is the worst case rather than
    /// a missing one: a till nobody has ever heard from is either broken or in
    /// somebody's drawer, and a null last-seen date would drop it from a report
    /// written the obvious way.
    /// </remarks>
    private async Task<DashboardExceptionPanel> OfflineDevicesAsync(
        DashboardQuery query,
        DateTimeOffset now,
        TimeSpan offlineAfter,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = now - offlineAfter;

        var rows = await (
            from device in Scoped(
                context.Set<Device>().AsNoTracking().Where(d => d.Status == DeviceStatus.Active),
                query,
                d => query.Locations.Contains(d.LocationId))
            join location in context.Locations.AsNoTracking() on device.LocationId equals location.Id
            where device.LastSeenAtUtc == null || device.LastSeenAtUtc < cutoff
            select new { DeviceId = device.Id.Value, device.ShortCode, location.Code, device.LastSeenAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new DashboardExceptionPanel(
            DashboardExceptionKind.OfflineDevices,
            NotificationSeverity.Warning,
            rows.Count,
            rows.Count == 0 ? null : rows.Min(r => r.LastSeenAtUtc),
            null,
            [
                .. rows
                    .OrderBy(r => r.LastSeenAtUtc ?? DateTimeOffset.MinValue)
                    .Take(sampleSize)
                    .Select(r => new DashboardExceptionItem(
                        r.DeviceId,
                        r.ShortCode,
                        r.Code,
                        r.LastSeenAtUtc,
                        r.LastSeenAtUtc is { } seen
                            ? FormattableString.Invariant($"Last heard from {seen:yyyy-MM-dd HH:mm} UTC")
                            : "Never heard from since enrolment")),
            ]);
    }

    /// <summary>
    /// Applies the caller's store scope to a query, or leaves it alone.
    /// </summary>
    /// <remarks>
    /// The predicate is written out at each call site rather than derived from a
    /// key selector. Building one by hand from an expression tree worked and was
    /// unreadable, and this is a security filter: it has to be obvious.
    /// </remarks>
    private static IQueryable<T> Scoped<T>(
        IQueryable<T> source,
        DashboardQuery query,
        System.Linq.Expressions.Expression<Func<T, bool>> inScope)
        => query.Locations.Count == 0 ? source : source.Where(inScope);

    private async Task<Dictionary<Guid, string>> CodeMapAsync(
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

    private static string Code(Dictionary<Guid, string> codes, LocationId id)
        => codes.TryGetValue(id.Value, out string? code) ? code : string.Empty;
}
