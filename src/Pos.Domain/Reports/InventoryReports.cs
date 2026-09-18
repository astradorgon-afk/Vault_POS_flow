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

/// <summary>How long stock has been standing.</summary>
/// <remarks>
/// <see cref="Unknown"/> is not a gap in the data to be tidied away. A product
/// that does not track batches has no received date anywhere in the system, so
/// its age is genuinely unknown; folding it into the youngest bucket would make
/// the report say the opposite of the truth about the stock most likely to be
/// old.
/// </remarks>
public enum InventoryAgeBucket
{
    /// <summary>The stock carries no received date, because its product tracks no batches.</summary>
    Unknown = 0,

    /// <summary>Received within the last 30 days.</summary>
    UpTo30Days = 1,

    /// <summary>31 to 60 days old.</summary>
    Days31To60 = 2,

    /// <summary>61 to 90 days old.</summary>
    Days61To90 = 3,

    /// <summary>91 to 180 days old.</summary>
    Days91To180 = 4,

    /// <summary>More than 180 days old.</summary>
    Over180Days = 5,
}

/// <summary>How much stock sits in one age bucket.</summary>
/// <param name="Bucket">The bucket.</param>
/// <param name="Quantity">The quantity in it.</param>
public sealed record InventoryAgeQuantity(
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryAgeBucket>))]
    InventoryAgeBucket Bucket,
    decimal Quantity);

/// <summary>
/// How old the stock of one product at one location is.
/// </summary>
/// <remarks>
/// Age is measured from the batch's received date, which is when the business
/// took custody — not from when it was manufactured, which is the supplier's
/// business, and not from the last movement, which would reset every time a
/// single unit sold and report a pallet standing since spring as new.
/// </remarks>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="ProductName">Its name.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="OnHand">Everything physically at the location.</param>
/// <param name="OldestReceivedOn">The received date of the oldest stock still held, when known.</param>
/// <param name="ByAge">The breakdown, buckets holding nothing omitted.</param>
public sealed record InventoryAgeingRow(
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid LocationId,
    string LocationCode,
    decimal OnHand,
    DateOnly? OldestReceivedOn,
    IReadOnlyList<InventoryAgeQuantity> ByAge);

/// <summary>
/// Stock that is not moving.
/// </summary>
/// <remarks>
/// <para>
/// A product that has <b>never</b> sold reports a null
/// <paramref name="LastSoldAtUtc"/> and is the worst case rather than a missing
/// one: a filter written as "last sold before X" would quietly drop exactly the
/// stock this report exists to find.
/// </para>
/// <para>
/// <paramref name="DaysOfCover"/> is how long the stock on hand would last at the
/// rate it sold during the window, and is null when nothing sold — there is no
/// rate to divide by, and reporting infinity as a large number is how a line
/// nobody can shift ends up looking merely slow. It is deliberately not called
/// turnover: a true turnover ratio needs the average stock held across the
/// period, and the system keeps balances rather than a history of them.
/// </para>
/// </remarks>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="ProductName">Its name.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="OnHand">What is standing there.</param>
/// <param name="UnitsSold">Units sold during the window.</param>
/// <param name="LastSoldAtUtc">When it last sold, or null if it never has.</param>
/// <param name="DaysSinceLastSale">Days since that sale, or null if it never has.</param>
/// <param name="DaysOfCover">How long the stock would last at the window's rate, or null.</param>
public sealed record InventoryDeadStockRow(
    Guid ProductId,
    string Sku,
    string ProductName,
    Guid LocationId,
    string LocationCode,
    decimal OnHand,
    decimal UnitsSold,
    DateTimeOffset? LastSoldAtUtc,
    int? DaysSinceLastSale,
    decimal? DaysOfCover);

/// <summary>Stock that is not moving, over a window.</summary>
/// <param name="FromUtc">The start of the window sales were counted over.</param>
/// <param name="ToUtc">The end of it.</param>
/// <param name="Rows">The rows, longest unsold first.</param>
/// <param name="Truncated">Whether more matched than were returned.</param>
public sealed record InventoryDeadStockReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<InventoryDeadStockRow> Rows,
    bool Truncated);
