using System.Text.Json.Serialization;
using Pos.Domain.Inventory;
using Pos.Domain.Quarantine;

namespace Pos.Domain.Reports;

/// <summary>
/// One recorded action, as an investigation reads it.
/// </summary>
/// <remarks>
/// The before-and-after payloads are deliberately absent. They can carry customer
/// details, prices and credentials-adjacent fields, and an activity report is
/// read far more often than a single entry is examined; whoever needs the payload
/// opens the entry. What is here is enough to see who did what, where, and under
/// what authority.
/// </remarks>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="Action">What was done.</param>
/// <param name="EntityType">To what kind of thing.</param>
/// <param name="EntityId">To which one.</param>
/// <param name="UserId">Who did it, where a person did.</param>
/// <param name="UserRoleSnapshot">What authority they held at the time, not now.</param>
/// <param name="DeviceId">Which register, where one was involved.</param>
/// <param name="LocationId">Where.</param>
/// <param name="LocationCode">That location's code.</param>
/// <param name="Reason">The reason they gave, where one was required.</param>
/// <param name="ReferenceNumber">The document it concerned, where there was one.</param>
/// <param name="CorrelationId">The request it belonged to, for pulling the rest of the story.</param>
public sealed record AuditActivityRow(
    DateTimeOffset OccurredAtUtc,
    string Action,
    string EntityType,
    Guid? EntityId,
    Guid? UserId,
    string? UserRoleSnapshot,
    Guid? DeviceId,
    Guid? LocationId,
    string LocationCode,
    string? Reason,
    Guid? ReferenceNumber,
    Guid CorrelationId);

/// <summary>A window of audit activity.</summary>
/// <param name="FromUtc">The start of the window.</param>
/// <param name="ToUtc">The end of it.</param>
/// <param name="Rows">The entries, newest first.</param>
/// <param name="Truncated">Whether more matched than were returned.</param>
public sealed record AuditActivityReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<AuditActivityRow> Rows,
    bool Truncated);

/// <summary>
/// Stock that arrived without authority, and what became of it.
/// </summary>
/// <remarks>
/// <c>UnidentifiedLines</c> counts the lines whose goods the catalogue does not
/// know. That is the whole point of the record: unidentifiable stock is the most
/// worth investigating, and a report that could only show recognised products
/// would leave it out.
/// </remarks>
/// <param name="IncidentId">The incident.</param>
/// <param name="Number">Its number, as printed.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="LocationId">Where the goods turned up.</param>
/// <param name="LocationCode">That location's code.</param>
/// <param name="CreatedAtUtc">When it was raised.</param>
/// <param name="ResolvedAtUtc">When it was closed, if it has been.</param>
/// <param name="DaysOpen">How long it has been open, or how long it took.</param>
/// <param name="LineCount">How many lines.</param>
/// <param name="Quantity">How many units are held.</param>
/// <param name="UnidentifiedLines">How many lines name goods the catalogue does not know.</param>
public sealed record QuarantineIncidentRow(
    Guid IncidentId,
    string Number,
    [property: JsonConverter(typeof(JsonStringEnumConverter<QuarantineIncidentStatus>))]
    QuarantineIncidentStatus Status,
    Guid LocationId,
    string LocationCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    int DaysOpen,
    int LineCount,
    decimal Quantity,
    int UnidentifiedLines);

/// <summary>
/// Stock that has expired or is about to, still held.
/// </summary>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="ProductName">Its name.</param>
/// <param name="LocationId">Where it is held.</param>
/// <param name="LocationCode">That location's code.</param>
/// <param name="BatchId">The batch.</param>
/// <param name="LotNumber">Its lot number.</param>
/// <param name="ExpiresOn">When it expires.</param>
/// <param name="DaysToExpiry">Days until then; negative once it has passed.</param>
/// <param name="State">The bucket it sits in.</param>
/// <param name="Quantity">How much.</param>
public sealed record ExpiringStockRow(
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid LocationId,
    string LocationCode,
    Guid BatchId,
    string LotNumber,
    DateOnly ExpiresOn,
    int DaysToExpiry,
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryState>))]
    InventoryState State,
    decimal Quantity);
