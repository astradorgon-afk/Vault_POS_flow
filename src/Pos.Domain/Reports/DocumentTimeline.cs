using System.Text.Json.Serialization;
using Pos.Domain.Inventory;

namespace Pos.Domain.Reports;

/// <summary>Where one line of a timeline came from.</summary>
/// <remarks>
/// Said out loud on every entry, because a person chasing a discrepancy needs to
/// know whether they are looking at something somebody did, something the ledger
/// posted, or something head office decided about an upload. Those three read very
/// differently and a merged list without the label invites reading one as another.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TimelineSource>))]
public enum TimelineSource
{
    /// <summary>A recorded human action.</summary>
    Audit = 1,

    /// <summary>A posting to the inventory ledger.</summary>
    Ledger = 2,

    /// <summary>A verdict on an event a register uploaded.</summary>
    Sync = 3,
}

/// <summary>One line of a document's timeline.</summary>
/// <param name="Source">Where it came from.</param>
/// <param name="AtUtc">When the system recorded it.</param>
/// <param name="OccurredAtUtc">When it happened, where that is known and can differ.</param>
/// <param name="What">The action, movement type or verdict.</param>
/// <param name="Detail">One line saying what it was.</param>
/// <param name="WhoUserId">Who did it, where a person did.</param>
/// <param name="WhoRoleSnapshot">The authority they held at the time, not now.</param>
/// <param name="DeviceId">The register involved, where one was.</param>
/// <param name="LocationId">Where.</param>
/// <param name="CorrelationId">The request it belonged to, for pulling the rest of the story.</param>
public sealed record DocumentTimelineEntry(
    TimelineSource Source,
    DateTimeOffset AtUtc,
    DateTimeOffset? OccurredAtUtc,
    string What,
    string Detail,
    Guid? WhoUserId,
    string? WhoRoleSnapshot,
    Guid? DeviceId,
    Guid? LocationId,
    Guid? CorrelationId);

/// <summary>One leg of a movement group.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="State">The bucket moved.</param>
/// <param name="BatchId">The batch, where the product tracks them.</param>
/// <param name="QuantityDelta">How much, signed.</param>
/// <param name="ValueDelta">What it was worth, or null without the financial permission.</param>
public sealed record MovementLegView(
    Guid ProductId,
    string Sku,
    Guid LocationId,
    string LocationCode,
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryState>))]
    InventoryState State,
    Guid? BatchId,
    decimal QuantityDelta,
    decimal? ValueDelta);

/// <summary>
/// One posting to the ledger, with every leg it balanced across.
/// </summary>
/// <remarks>
/// The legs are shown together because that is the only way a posting makes sense:
/// stock leaving one bucket always arrives somewhere, and a chain that showed only
/// the side you asked about would look like stock appearing from nowhere.
/// </remarks>
/// <param name="MovementGroupId">The posting.</param>
/// <param name="EventId">The business event it was posted under.</param>
/// <param name="MovementType">Why the stock moved.</param>
/// <param name="RecordedAtUtc">When the ledger wrote it.</param>
/// <param name="OccurredAtUtc">When it happened, by the reporting device's account.</param>
/// <param name="ReversesMovementGroupId">The posting this one reverses, where it reverses one.</param>
/// <param name="ReversedByMovementGroupId">The posting that reversed this one, where one did.</param>
/// <param name="Legs">Every leg, source side first.</param>
public sealed record MovementGroupView(
    Guid MovementGroupId,
    Guid EventId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryMovementType>))]
    InventoryMovementType MovementType,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset OccurredAtUtc,
    Guid? ReversesMovementGroupId,
    Guid? ReversedByMovementGroupId,
    IReadOnlyList<MovementLegView> Legs);

/// <summary>
/// Everything that happened to one document.
/// </summary>
/// <remarks>
/// Ordered by when the system recorded each thing, not by when it happened. An
/// offline sale uploaded on Tuesday occurred on Monday, and a timeline sorted by
/// occurrence would show its ledger posting before the shift that contained it.
/// Both times are carried, so a reader can see the gap rather than be protected
/// from it.
/// </remarks>
/// <param name="ReferenceDocumentType">What kind of document.</param>
/// <param name="ReferenceDocumentId">Which one.</param>
/// <param name="ReferenceNumber">Its number as printed, where the ledger recorded one.</param>
/// <param name="Entries">The timeline, oldest first.</param>
/// <param name="Movements">The ledger postings it caused, oldest first.</param>
public sealed record DocumentTimeline(
    [property: JsonConverter(typeof(JsonStringEnumConverter<ReferenceDocumentType>))]
    ReferenceDocumentType ReferenceDocumentType,
    Guid ReferenceDocumentId,
    string? ReferenceNumber,
    IReadOnlyList<DocumentTimelineEntry> Entries,
    IReadOnlyList<MovementGroupView> Movements);
