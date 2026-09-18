using System.Text.Json.Serialization;
using Pos.Domain.Inventory;

namespace Pos.Domain.Reports;

/// <summary>How much stock sits in one state.</summary>
/// <param name="State">The state.</param>
/// <param name="Quantity">The quantity in it.</param>
public sealed record InventoryStateQuantity(
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryState>))]
    InventoryState State,
    decimal Quantity);

/// <summary>
/// What is on the shelf, for one product at one location.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="Available"/> is the only number a till may sell from, which is
/// why it is a column of its own rather than something a reader has to pick out
/// of the breakdown. <paramref name="OnHand"/> is everything physically standing
/// there, sellable or not; stock dispatched on a transfer and not yet received is
/// still the business's but is not at the location, so it is counted separately.
/// </para>
/// <para>
/// Carries no money. What stock cost is a financial question with a permission of
/// its own, and a shelf count that quietly discloses margin is how an operational
/// report becomes one only head office may open.
/// </para>
/// </remarks>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="ProductName">Its name.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="LocationName">Its name.</param>
/// <param name="Available">What may be sold.</param>
/// <param name="OnHand">Everything physically at the location.</param>
/// <param name="InFlight">Dispatched and not yet received, or held as a transit variance.</param>
/// <param name="ByState">The full breakdown, states holding nothing omitted.</param>
public sealed record InventoryOnHandRow(
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid LocationId,
    string LocationCode,
    string LocationName,
    decimal Available,
    decimal OnHand,
    decimal InFlight,
    IReadOnlyList<InventoryStateQuantity> ByState);

/// <summary>What stock is worth, for one product at one location.</summary>
/// <remarks>
/// <paramref name="AverageUnitCost"/> is derived from the totals, never averaged
/// from the batch buckets underneath: an average of averages weights a batch
/// holding one unit the same as a batch holding a thousand. Where the quantity is
/// zero it is zero too, because there is no unit to state a cost per — a bucket
/// emptied at a value that did not quite reach zero is a rounding residue to
/// investigate, not a unit cost of infinity.
/// </remarks>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="ProductName">Its name.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="LocationName">Its name.</param>
/// <param name="Quantity">The quantity valued.</param>
/// <param name="AverageUnitCost">The value divided by the quantity.</param>
/// <param name="TotalValue">What it is worth.</param>
public sealed record InventoryValuationRow(
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid LocationId,
    string LocationCode,
    string LocationName,
    decimal Quantity,
    decimal AverageUnitCost,
    decimal TotalValue);

/// <summary>A stock valuation, with its own total.</summary>
/// <param name="AsOfUtc">When it was taken. A valuation is a statement about a moment.</param>
/// <param name="Quantity">Everything counted.</param>
/// <param name="TotalValue">What all of it is worth.</param>
/// <param name="Rows">The rows, most valuable first.</param>
/// <param name="Truncated">Whether more rows matched than were returned.</param>
public sealed record InventoryValuationReport(
    DateTimeOffset AsOfUtc,
    decimal Quantity,
    decimal TotalValue,
    IReadOnlyList<InventoryValuationRow> Rows,
    bool Truncated);

/// <summary>
/// One leg of the ledger, as a person reads it.
/// </summary>
/// <remarks>
/// Ordered by <paramref name="RecordedAtUtc"/> rather than when it happened,
/// because that is the order the ledger was actually written in and the only one
/// that reconciles — an offline sale uploaded on Tuesday occurred on Monday, and
/// a history that reordered it would never tie back to a balance.
/// </remarks>
/// <param name="OccurredAtUtc">When it happened, by the reporting device's account.</param>
/// <param name="RecordedAtUtc">When the ledger wrote it.</param>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="ProductName">Its name.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="State">The bucket the leg moved.</param>
/// <param name="QuantityDelta">How much, signed.</param>
/// <param name="MovementType">Why.</param>
/// <param name="ReferenceDocumentType">What kind of document.</param>
/// <param name="ReferenceNumber">Which document, as it is printed.</param>
/// <param name="BatchId">The batch, when the product tracks them.</param>
/// <param name="CreatedByUserId">Who caused it.</param>
public sealed record InventoryMovementRow(
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid LocationId,
    string LocationCode,
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryState>))]
    InventoryState State,
    decimal QuantityDelta,
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryMovementType>))]
    InventoryMovementType MovementType,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ReferenceDocumentType>))]
    ReferenceDocumentType ReferenceDocumentType,
    string ReferenceNumber,
    Guid? BatchId,
    Guid CreatedByUserId);

/// <summary>A page of movement history.</summary>
/// <param name="FromUtc">The start of the window.</param>
/// <param name="ToUtc">The end of the window.</param>
/// <param name="Rows">The legs, oldest first.</param>
/// <param name="Truncated">Whether the window holds more than were returned.</param>
public sealed record InventoryMovementReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<InventoryMovementRow> Rows,
    bool Truncated);
