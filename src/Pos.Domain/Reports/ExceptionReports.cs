using System.Text.Json.Serialization;
using Pos.Domain.Inventory;

namespace Pos.Domain.Reports;

/// <summary>
/// Stock written off or corrected, grouped by why.
/// </summary>
/// <remarks>
/// Quantity only. What the write-off cost is the shrinkage report, which needs
/// <c>report.view.financial</c>: how much stock went missing is an operational
/// question a stockroom answers, and what it was worth is the one an owner asks.
/// </remarks>
/// <param name="MovementType">What kind of adjustment.</param>
/// <param name="ReasonCode">The reason recorded, where the type requires one.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="Entries">How many ledger legs.</param>
/// <param name="QuantityOut">Units that left, as a positive number.</param>
/// <param name="QuantityIn">Units that were added back.</param>
public sealed record AdjustmentSummaryRow(
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryMovementType>))]
    InventoryMovementType MovementType,
    [property: JsonConverter(typeof(JsonStringEnumConverter<AdjustmentReasonCode>))]
    AdjustmentReasonCode? ReasonCode,
    Guid LocationId,
    string LocationCode,
    int Entries,
    decimal QuantityOut,
    decimal QuantityIn);

/// <summary>
/// What the losses cost, grouped by why.
/// </summary>
/// <remarks>
/// The value is taken from the ledger leg, which recorded what the stock was
/// carried at when it left — not what it would cost to replace today. A shrinkage
/// figure re-valued at current cost would move every time a supplier changed a
/// price, and would no longer reconcile to the accounts it is meant to explain.
/// </remarks>
/// <param name="MovementType">What kind of loss.</param>
/// <param name="ReasonCode">The reason recorded, where the type requires one.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="Entries">How many ledger legs.</param>
/// <param name="Quantity">Units lost, as a positive number.</param>
/// <param name="Value">What they were carried at.</param>
public sealed record ShrinkageRow(
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryMovementType>))]
    InventoryMovementType MovementType,
    [property: JsonConverter(typeof(JsonStringEnumConverter<AdjustmentReasonCode>))]
    AdjustmentReasonCode? ReasonCode,
    Guid LocationId,
    string LocationCode,
    int Entries,
    decimal Quantity,
    decimal Value);

/// <summary>What the losses cost, with the total they add up to.</summary>
/// <param name="FromUtc">The start of the window.</param>
/// <param name="ToUtc">The end of it.</param>
/// <param name="TotalQuantity">Everything lost.</param>
/// <param name="TotalValue">What all of it was carried at.</param>
/// <param name="Rows">The rows, most valuable loss first.</param>
public sealed record ShrinkageReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    decimal TotalQuantity,
    decimal TotalValue,
    IReadOnlyList<ShrinkageRow> Rows);

/// <summary>
/// One line of a physical count that did not match the system.
/// </summary>
/// <remarks>
/// A line that was never counted is not a variance of zero. It reports a null
/// <paramref name="PhysicalQuantity"/> and a null <paramref name="Variance"/>,
/// because "we looked and it was right" and "nobody looked" are different facts,
/// and a count report that conflates them makes an unfinished count read as a
/// clean one.
/// </remarks>
/// <param name="CountId">The count.</param>
/// <param name="CountNumber">Its number, as printed.</param>
/// <param name="LocationId">Where it was taken.</param>
/// <param name="LocationCode">That location's code.</param>
/// <param name="Status">Where the count has got to.</param>
/// <param name="CountedAtUtc">When the line was counted, if it was.</param>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="ProductName">Its name.</param>
/// <param name="SystemQuantity">What the system said.</param>
/// <param name="PhysicalQuantity">What was counted, or null if nobody counted it.</param>
/// <param name="Variance">Counted less system, or null if nobody counted it.</param>
/// <param name="VarianceValue">What the variance was carried at.</param>
/// <param name="IsRepeatVariance">Whether this product varied at this location last time too.</param>
public sealed record CountVarianceRow(
    Guid CountId,
    string CountNumber,
    Guid LocationId,
    string LocationCode,
    [property: JsonConverter(typeof(JsonStringEnumConverter<InventoryCountStatus>))]
    InventoryCountStatus Status,
    DateTimeOffset? CountedAtUtc,
    Guid ProductId,
    string Sku,
    string ProductName,
    decimal SystemQuantity,
    decimal? PhysicalQuantity,
    decimal? Variance,
    decimal? VarianceValue,
    bool IsRepeatVariance);

/// <summary>A window of count variances.</summary>
/// <param name="FromUtc">The start of the window.</param>
/// <param name="ToUtc">The end of it.</param>
/// <param name="Rows">The lines, largest absolute variance first.</param>
/// <param name="Truncated">Whether more matched than were returned.</param>
public sealed record CountVarianceReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<CountVarianceRow> Rows,
    bool Truncated);
