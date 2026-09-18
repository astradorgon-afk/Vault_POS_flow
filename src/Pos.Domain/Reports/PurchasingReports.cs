using System.Text.Json.Serialization;
using Pos.Domain.Purchasing;

namespace Pos.Domain.Reports;

/// <summary>
/// One purchase order, as its progress reads.
/// </summary>
/// <remarks>
/// Carries the order's value. That is not margin and not a stock valuation: it is
/// what the business agreed to pay a supplier, and the person who raises and
/// receives orders cannot do the job without seeing it. Cost of goods and what
/// they are now worth stay behind <c>report.view.financial</c>.
/// </remarks>
/// <param name="PurchaseOrderId">The order.</param>
/// <param name="Number">Its PO number, or null while it is still a draft.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="SupplierId">Who it was placed with.</param>
/// <param name="SupplierName">Their name.</param>
/// <param name="DestinationLocationId">Where the goods were going.</param>
/// <param name="DestinationCode">That location's code.</param>
/// <param name="CreatedAtUtc">When it was raised.</param>
/// <param name="OrderedAtUtc">When it was placed, if it has been.</param>
/// <param name="ExpectedAtUtc">When the supplier said it would arrive.</param>
/// <param name="FirstReceivedAtUtc">When the first goods receipt was booked.</param>
/// <param name="LineCount">How many product lines.</param>
/// <param name="OrderedQuantity">What was ordered.</param>
/// <param name="ReceivedQuantity">What has been booked in, accepted or not.</param>
/// <param name="AcceptedQuantity">What was taken into stock.</param>
/// <param name="CurrencyCode">The order's currency.</param>
/// <param name="GrandTotal">What was agreed.</param>
public sealed record PurchaseOrderReportRow(
    Guid PurchaseOrderId,
    string? Number,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PurchaseOrderStatus>))]
    PurchaseOrderStatus Status,
    Guid SupplierId,
    string SupplierName,
    Guid DestinationLocationId,
    string DestinationCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? OrderedAtUtc,
    DateTimeOffset? ExpectedAtUtc,
    DateTimeOffset? FirstReceivedAtUtc,
    int LineCount,
    decimal OrderedQuantity,
    decimal ReceivedQuantity,
    decimal AcceptedQuantity,
    string CurrencyCode,
    decimal GrandTotal);

/// <summary>A window of purchase orders.</summary>
/// <param name="FromUtc">The start of the window.</param>
/// <param name="ToUtc">The end of it.</param>
/// <param name="Rows">The orders, newest first.</param>
/// <param name="Truncated">Whether more matched than were returned.</param>
public sealed record PurchaseOrderReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<PurchaseOrderReportRow> Rows,
    bool Truncated);

/// <summary>
/// How a supplier has performed over a window.
/// </summary>
/// <remarks>
/// <para>
/// Every rate here is reported beside the count it was computed from, because a
/// supplier who delivered once, late, scores 0% on time and reads identically to
/// one who failed forty times. The denominator is the difference between a
/// verdict and an anecdote.
/// </para>
/// <para>
/// <paramref name="OnTimeRate"/> is null when no order in the window carried both
/// an expected date and a receipt. Scoring an unpromised delivery as on time
/// would reward a supplier for refusing to commit to a date.
/// </para>
/// </remarks>
/// <param name="SupplierId">The supplier.</param>
/// <param name="SupplierCode">Their code.</param>
/// <param name="SupplierName">Their name.</param>
/// <param name="OrdersPlaced">Orders placed in the window.</param>
/// <param name="OrdersReceived">How many of them have had goods booked in.</param>
/// <param name="OrdersScoredForTime">How many could be scored for punctuality.</param>
/// <param name="OnTimeRate">Of those, the fraction that arrived on or before the promised date.</param>
/// <param name="AverageDaysLate">Of those, the mean lateness in days; negative means early.</param>
/// <param name="OrderedQuantity">What was ordered.</param>
/// <param name="AcceptedQuantity">What was taken into stock.</param>
/// <param name="FillRate">Accepted over ordered, or null when nothing was ordered.</param>
/// <param name="RejectedQuantity">What arrived damaged, wrong, expired or over tolerance.</param>
/// <param name="ReceiptsWithMissingDocuments">Deliveries booked in without their paperwork.</param>
/// <param name="ReceiptsWithCostVariance">Deliveries whose cost did not match the order.</param>
public sealed record SupplierPerformanceRow(
    Guid SupplierId,
    string SupplierCode,
    string SupplierName,
    int OrdersPlaced,
    int OrdersReceived,
    int OrdersScoredForTime,
    decimal? OnTimeRate,
    decimal? AverageDaysLate,
    decimal OrderedQuantity,
    decimal AcceptedQuantity,
    decimal? FillRate,
    decimal RejectedQuantity,
    int ReceiptsWithMissingDocuments,
    int ReceiptsWithCostVariance);
