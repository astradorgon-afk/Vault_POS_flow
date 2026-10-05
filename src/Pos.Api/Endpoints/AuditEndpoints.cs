using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Pos.Api.Authorization;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Transfers;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>Read-only search over the audit log and transfer custody timeline.</summary>
public static class AuditEndpoints
{
    private const int MaxPageSize = 100;
    private static readonly JsonSerializerOptions TransferDetailsJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/audit", ListAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewAudit) { Scope = ScopeSource.None })
            .WithTags("Audit")
            .WithName("ListAuditEntries")
            .WithSummary("Searches recorded business activity.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PosDbContext context,
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] Guid? userId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (from is { } startDate && to is { } endDate && startDate > endDate)
        {
            return TypedResults.BadRequest(new { error = "The start date must be on or before the end date." });
        }

        IQueryable<AuditLogEntry> query = context.AuditLog.AsNoTracking();
        IQueryable<TransferCustodyEvent> transferEvents = context.TransferCustodyEvents.AsNoTracking();
        if (from is { } fromDate)
        {
            DateTimeOffset start = new(fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            query = query.Where(entry => entry.OccurredAtUtc >= start);
        }

        if (to is { } toDate)
        {
            DateTimeOffset exclusiveEnd = new(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            query = query.Where(entry => entry.OccurredAtUtc < exclusiveEnd);
            transferEvents = transferEvents.Where(entry => entry.OccurredAtUtc < exclusiveEnd);
        }

        if (from is { } transferFromDate)
        {
            DateTimeOffset transferStart = new(transferFromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            transferEvents = transferEvents.Where(entry => entry.OccurredAtUtc >= transferStart);
        }

        if (userId is { } actorId)
        {
            UserId actor = new(actorId);
            query = query.Where(entry => entry.UserId == actor);
            transferEvents = transferEvents.Where(entry => entry.ActorUserId == actor);
        }

        query = ApplyCategory(query, category);
        if (IsTransferCategoryExcluded(category))
        {
            transferEvents = transferEvents.Where(_ => false);
        }

        string term = search?.Trim() ?? string.Empty;
        if (term.Length > 0)
        {
            string pattern = $"%{term.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            query = query.Where(entry =>
                EF.Functions.Like(entry.Action, pattern, "\\") ||
                EF.Functions.Like(entry.EntityType, pattern, "\\") ||
                (entry.Reason != null && EF.Functions.Like(entry.Reason, pattern, "\\")) ||
                (entry.UserRoleSnapshot != null && EF.Functions.Like(entry.UserRoleSnapshot, pattern, "\\")) ||
                (entry.PreviousValueJson != null && EF.Functions.Like(entry.PreviousValueJson, pattern, "\\")) ||
                (entry.NewValueJson != null && EF.Functions.Like(entry.NewValueJson, pattern, "\\")));

            TransferCustodyEventKind[] matchingKinds = Enum.GetValues<TransferCustodyEventKind>()
                .Where(kind => TransferAction(kind).Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            IQueryable<TransferOrderId> productMatches = context.TransferLines.AsNoTracking()
                .Where(line => context.Products.AsNoTracking().Any(product =>
                    product.Id == line.ProductId && EF.Functions.Like(product.Name, pattern, "\\")))
                .Select(line => line.TransferOrderId);
            transferEvents = transferEvents.Where(entry =>
                (entry.Note != null && EF.Functions.Like(entry.Note, pattern, "\\")) ||
                matchingKinds.Contains(entry.Kind) ||
                productMatches.Contains(entry.TransferOrderId) ||
                context.Transfers.AsNoTracking().Any(transfer =>
                    transfer.Id == entry.TransferOrderId &&
                    (EF.Functions.Like(transfer.Number, pattern, "\\") ||
                     (transfer.ShipmentNumber != null && EF.Functions.Like(transfer.ShipmentNumber, pattern, "\\")) ||
                     (transfer.ReceiptNumber != null && EF.Functions.Like(transfer.ReceiptNumber, pattern, "\\")))));
        }

        int total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        int transferTotal = await transferEvents.CountAsync(cancellationToken).ConfigureAwait(false);
        total += transferTotal;
        int safeOffset = Math.Max(0, offset);
        int safeLimit = Math.Clamp(limit, 1, MaxPageSize);
        int fetchCount = (int)Math.Min((long)safeOffset + safeLimit, int.MaxValue);
        List<ActivityCandidate> auditCandidates = await query
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(fetchCount)
            .Select(entry => new ActivityCandidate(false, entry.Id.Value, entry.OccurredAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ActivityCandidate> transferCandidates = await transferEvents
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(fetchCount)
            .Select(entry => new ActivityCandidate(true, entry.Id.Value, entry.OccurredAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ActivityCandidate> candidates = [.. auditCandidates.Concat(transferCandidates)
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(fetchCount)];
        ActivityCandidate[] page = [.. candidates.Skip(safeOffset).Take(safeLimit)];

        AuditLogId[] auditEntryIds = [.. page.Where(candidate => !candidate.IsTransfer).Select(candidate => new AuditLogId(candidate.Id))];
        TransferCustodyEventId[] transferEventIds = [.. page.Where(candidate => candidate.IsTransfer).Select(candidate => new TransferCustodyEventId(candidate.Id))];
        List<AuditLogEntry> entries = auditEntryIds.Length == 0
            ? []
            : await context.AuditLog.AsNoTracking()
                .Where(entry => auditEntryIds.Contains(entry.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        List<TransferCustodyEvent> selectedTransferEvents = transferEventIds.Length == 0
            ? []
            : await context.TransferCustodyEvents.AsNoTracking()
                .Where(entry => transferEventIds.Contains(entry.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        Guid[] actorIds = [.. entries.Where(entry => entry.UserId is not null)
            .Select(entry => entry.UserId!.Value.Value)
            .Concat(selectedTransferEvents.Select(entry => entry.ActorUserId.Value)).Distinct()];
        Guid[] locationIds = [.. entries.Where(entry => entry.LocationId is not null)
            .Select(entry => entry.LocationId!.Value.Value).Distinct()];
        Dictionary<Guid, string> actorNames = actorIds.Length == 0
            ? []
            : await context.Users.AsNoTracking()
                .Where(user => actorIds.Contains(user.Id))
                .Select(user => new { user.Id, user.DisplayName })
                .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken)
                .ConfigureAwait(false);
        Dictionary<Guid, string> locationNames = [];
        if (locationIds.Length > 0)
        {
            HashSet<Guid> requestedLocations = [.. locationIds];
            List<Pos.Domain.Organizations.Location> locations = await context.Locations.AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            locationNames = locations
                .Where(location => requestedLocations.Contains(location.Id.Value))
                .ToDictionary(location => location.Id.Value, location => location.Name);
        }

        Dictionary<Guid, AuditLogEntry> entriesById = entries.ToDictionary(entry => entry.Id.Value);
        Dictionary<Guid, TransferCustodyEvent> transferEventsById = selectedTransferEvents.ToDictionary(entry => entry.Id.Value);
        TransferOrderId[] transferIds = [.. selectedTransferEvents.Select(entry => entry.TransferOrderId).Distinct()];
        Dictionary<Guid, Transfer> transfersById = transferIds.Length == 0
            ? []
            : await context.Transfers.AsNoTracking().AsSplitQuery()
                .Include(transfer => transfer.Lines)
                .Include(transfer => transfer.Allocations)
                .Where(transfer => transferIds.Contains(transfer.Id))
                .ToDictionaryAsync(transfer => transfer.Id.Value, cancellationToken)
                .ConfigureAwait(false);
        ProductId[] productIds = [.. transfersById.Values.SelectMany(transfer => transfer.Lines).Select(line => line.ProductId).Distinct()];
        Dictionary<Guid, string> productNames = productIds.Length == 0
            ? []
            : await context.Products.AsNoTracking()
                .Where(product => productIds.Contains(product.Id))
                .ToDictionaryAsync(product => product.Id.Value, product => product.Name, cancellationToken)
                .ConfigureAwait(false);
        LocationId[] transferLocationIds = [.. transfersById.Values
            .SelectMany(transfer => new[] { transfer.SourceLocationId, transfer.DestinationLocationId })
            .Distinct()];
        if (transferLocationIds.Length > 0)
        {
            Dictionary<Guid, string> transferLocationNames = await context.Locations.AsNoTracking()
                .Where(location => transferLocationIds.Contains(location.Id))
                .ToDictionaryAsync(location => location.Id.Value, location => location.Name, cancellationToken)
                .ConfigureAwait(false);
            foreach ((Guid id, string name) in transferLocationNames)
            {
                locationNames[id] = name;
            }
        }

        AuditLogItem[] items = [.. page.Select(candidate => candidate.IsTransfer
            ? CreateTransferItem(transferEventsById[candidate.Id], transfersById, actorNames, locationNames, productNames)
            : CreateAuditItem(entriesById[candidate.Id], actorNames, locationNames))];

        return TypedResults.Ok(new AuditLogPage(items, total, safeOffset, safeLimit));
    }

    private static IQueryable<AuditLogEntry> ApplyCategory(IQueryable<AuditLogEntry> query, string? category)
        => category?.Trim().ToLowerInvariant() switch
        {
            "transactions" => query.Where(entry => entry.Action.StartsWith("sale.") || entry.Action.StartsWith("customer.") || entry.Action.StartsWith("shift.")),
            "inventory" => query.Where(entry => entry.Action.StartsWith("inventory.") || entry.Action.StartsWith("quarantine.") || entry.Action.StartsWith("expiry.")),
            "transfers" => query.Where(entry => entry.Action.StartsWith("transfer.")),
            "purchasing" => query.Where(entry => entry.Action.StartsWith("purchase.")),
            "people" => query.Where(entry => entry.Action.StartsWith("user.") || entry.Action.StartsWith("role.") || entry.Action.StartsWith("settings.")),
            "security" => query.Where(entry => entry.Action.StartsWith("auth.") || entry.Action.StartsWith("device.")),
            "products" => query.Where(entry => entry.Action.StartsWith("product.")),
            "other" => query.Where(entry =>
                !entry.Action.StartsWith("sale.") && !entry.Action.StartsWith("customer.") && !entry.Action.StartsWith("shift.") &&
                !entry.Action.StartsWith("inventory.") && !entry.Action.StartsWith("quarantine.") && !entry.Action.StartsWith("expiry.") &&
                !entry.Action.StartsWith("transfer.") && !entry.Action.StartsWith("purchase.") &&
                !entry.Action.StartsWith("user.") && !entry.Action.StartsWith("role.") && !entry.Action.StartsWith("settings.") &&
                !entry.Action.StartsWith("auth.") && !entry.Action.StartsWith("device.") && !entry.Action.StartsWith("product.")),
            _ => query,
        };

    private static bool IsTransferCategoryExcluded(string? category)
        => category?.Trim().ToLowerInvariant() is "transactions" or "inventory" or "purchasing" or "people" or "security" or "products" or "other";

    private static string TransferAction(TransferCustodyEventKind kind) => kind switch
    {
        TransferCustodyEventKind.Created => AuditActions.Transfers.Created,
        TransferCustodyEventKind.Submitted => AuditActions.Transfers.Requested,
        TransferCustodyEventKind.Reviewed => AuditActions.Transfers.ReviewStarted,
        TransferCustodyEventKind.ApprovedWithAmendment or TransferCustodyEventKind.Approved or TransferCustodyEventKind.PreApprovedApproval => AuditActions.Transfers.Approved,
        TransferCustodyEventKind.Rejected => AuditActions.Transfers.Rejected,
        TransferCustodyEventKind.Picked => AuditActions.Transfers.Picked,
        TransferCustodyEventKind.Ready => AuditActions.Transfers.Ready,
        TransferCustodyEventKind.Dispatched => AuditActions.Transfers.Dispatched,
        TransferCustodyEventKind.DispatchCancelled => AuditActions.Transfers.DispatchCancelled,
        TransferCustodyEventKind.Received => AuditActions.Transfers.Received,
        TransferCustodyEventKind.Resolved => AuditActions.Transfers.DiscrepancyResolved,
        TransferCustodyEventKind.Verified => AuditActions.Transfers.Verified,
        TransferCustodyEventKind.EmergencyCreated => AuditActions.Transfers.EmergencyCreated,
        TransferCustodyEventKind.CentralReviewRatified or TransferCustodyEventKind.CentralReviewRejected => AuditActions.Transfers.EmergencyReviewed,
        _ => $"transfer.{kind.ToString().ToLowerInvariant()}",
    };

    private static AuditLogItem CreateAuditItem(
        AuditLogEntry entry,
        IReadOnlyDictionary<Guid, string> actorNames,
        IReadOnlyDictionary<Guid, string> locationNames)
        => new(
            entry.Id.Value,
            entry.OccurredAtUtc,
            entry.Action,
            entry.EntityType,
            null,
            entry.EntityId,
            entry.UserId?.Value,
            entry.UserId is { } id ? actorNames.GetValueOrDefault(id.Value, "Unknown user") : "System",
            entry.UserRoleSnapshot,
            entry.LocationId is { } location ? locationNames.GetValueOrDefault(location.Value, "Unknown location") : null,
            entry.Reason,
            entry.ReferenceDocumentType?.ToString(),
            entry.ReferenceDocumentId,
            entry.PreviousValueJson,
            entry.NewValueJson,
            entry.CorrelationId.Value);

    private static AuditLogItem CreateTransferItem(
        TransferCustodyEvent custodyEvent,
        Dictionary<Guid, Transfer> transfersById,
        IReadOnlyDictionary<Guid, string> actorNames,
        IReadOnlyDictionary<Guid, string> locationNames,
        IReadOnlyDictionary<Guid, string> productNames)
    {
        Transfer transfer = transfersById[custodyEvent.TransferOrderId.Value];
        Guid locationId = IsWarehouseSideEvent(custodyEvent.Kind)
            ? transfer.SourceLocationId.Value
            : transfer.DestinationLocationId.Value;
        object details = new
        {
            eventKind = custodyEvent.Kind.ToString(),
            eventSequence = custodyEvent.Sequence,
            transferNumber = string.IsNullOrWhiteSpace(transfer.Number) ? null : transfer.Number,
            shipmentNumber = transfer.ShipmentNumber,
            receiptNumber = transfer.ReceiptNumber,
            currentStatus = transfer.Status.ToString(),
            mode = transfer.Mode.ToString(),
            kind = transfer.Kind.ToString(),
            quantityBasis = "Current transfer totals",
            source = new { id = transfer.SourceLocationId.Value, name = locationNames.GetValueOrDefault(transfer.SourceLocationId.Value, "Unknown location") },
            destination = new { id = transfer.DestinationLocationId.Value, name = locationNames.GetValueOrDefault(transfer.DestinationLocationId.Value, "Unknown location") },
            products = transfer.Lines.Select(line => new
            {
                lineNo = line.LineNo,
                productId = line.ProductId.Value,
                productName = productNames.GetValueOrDefault(line.ProductId.Value, "Unknown product"),
                requestedQuantity = line.RequestedQuantity,
                pickedQuantity = transfer.Allocations.Where(allocation => allocation.LineNo == line.LineNo).Sum(allocation => allocation.Quantity),
                receivedQuantity = transfer.Allocations.Where(allocation => allocation.LineNo == line.LineNo).Sum(allocation => allocation.ReceivedQuantity),
                damagedQuantity = transfer.Allocations.Where(allocation => allocation.LineNo == line.LineNo).Sum(allocation => allocation.DamagedQuantity),
            }),
        };

        return new AuditLogItem(
            custodyEvent.Id.Value,
            custodyEvent.OccurredAtUtc,
            TransferAction(custodyEvent.Kind),
            "Transfer",
            string.Join(", ", transfer.Lines.Select(line => productNames.GetValueOrDefault(line.ProductId.Value, "Unknown product"))),
            transfer.Id.Value,
            custodyEvent.ActorUserId.Value,
            actorNames.GetValueOrDefault(custodyEvent.ActorUserId.Value, "Unknown user"),
            null,
            locationNames.GetValueOrDefault(locationId, "Unknown location"),
            custodyEvent.Note,
            "TransferOrder",
            transfer.Id.Value,
            null,
            JsonSerializer.Serialize(details, TransferDetailsJsonOptions),
            Guid.Empty);
    }

    private static bool IsWarehouseSideEvent(TransferCustodyEventKind kind)
        => kind is TransferCustodyEventKind.Picked or TransferCustodyEventKind.Ready or TransferCustodyEventKind.Dispatched or TransferCustodyEventKind.DispatchCancelled or
            TransferCustodyEventKind.EmergencyCreated or TransferCustodyEventKind.CentralReviewRatified or TransferCustodyEventKind.CentralReviewRejected;

    private sealed record ActivityCandidate(bool IsTransfer, Guid Id, DateTimeOffset OccurredAtUtc);
}

public sealed record AuditLogPage(IReadOnlyList<AuditLogItem> Items, int Total, int Offset, int Limit);

public sealed record AuditLogItem(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string Action,
    string EntityType,
    string? RecordSummary,
    Guid? EntityId,
    Guid? UserId,
    string ActorName,
    string? RoleSnapshot,
    string? LocationName,
    string? Reason,
    string? ReferenceDocumentType,
    Guid? ReferenceDocumentId,
    string? PreviousValueJson,
    string? NewValueJson,
    Guid CorrelationId);
