using System.Text.Json.Serialization;
using Pos.Domain.Transfers;

namespace Pos.Domain.Reports;

/// <summary>
/// One transfer, as the pipeline reads it.
/// </summary>
/// <remarks>
/// <para>
/// The quantities are three different questions and are reported as three
/// columns: what was asked for, what actually left the source, and what the
/// destination counted. Collapsing them into one number is how a shipment that
/// arrived two cartons short comes to look complete.
/// </para>
/// <para>
/// <paramref name="DaysInFlight"/> is only meaningful while stock is in transit,
/// so it is null once a transfer has been received. A transfer that has been
/// sitting dispatched for a fortnight is the row this report exists to surface.
/// </para>
/// </remarks>
/// <param name="TransferId">The transfer.</param>
/// <param name="Number">Its TRF number, as printed.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Kind">Warehouse to store, store to store, or store back to warehouse.</param>
/// <param name="Mode">Normal, pre-approved, or raised as an emergency.</param>
/// <param name="SourceLocationId">Where it left.</param>
/// <param name="SourceCode">That location's code.</param>
/// <param name="DestinationLocationId">Where it was going.</param>
/// <param name="DestinationCode">That location's code.</param>
/// <param name="CreatedAtUtc">When it was raised.</param>
/// <param name="DispatchedAtUtc">When the stock left, if it has.</param>
/// <param name="ReceivedAtUtc">When the destination counted it, if it has.</param>
/// <param name="LineCount">How many product lines.</param>
/// <param name="RequestedQuantity">What was asked for.</param>
/// <param name="DispatchedQuantity">What left the source.</param>
/// <param name="ReceivedQuantity">What the destination counted.</param>
/// <param name="DamagedQuantity">What arrived damaged.</param>
/// <param name="OpenDiscrepancies">How many discrepancies still have no resolution.</param>
/// <param name="DaysInFlight">How long the stock has been in transit, or null once received.</param>
public sealed record TransferReportRow(
    Guid TransferId,
    string Number,
    [property: JsonConverter(typeof(JsonStringEnumConverter<TransferStatus>))]
    TransferStatus Status,
    [property: JsonConverter(typeof(JsonStringEnumConverter<TransferKind>))]
    TransferKind Kind,
    [property: JsonConverter(typeof(JsonStringEnumConverter<TransferMode>))]
    TransferMode Mode,
    Guid SourceLocationId,
    string SourceCode,
    Guid DestinationLocationId,
    string DestinationCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? ReceivedAtUtc,
    int LineCount,
    decimal RequestedQuantity,
    decimal DispatchedQuantity,
    decimal ReceivedQuantity,
    decimal DamagedQuantity,
    int OpenDiscrepancies,
    int? DaysInFlight);

/// <summary>A window of transfers.</summary>
/// <param name="FromUtc">The start of the window.</param>
/// <param name="ToUtc">The end of it.</param>
/// <param name="Rows">The transfers, longest in flight first.</param>
/// <param name="Truncated">Whether more matched than were returned.</param>
public sealed record TransferReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<TransferReportRow> Rows,
    bool Truncated);

/// <summary>
/// How much moved along one source-to-destination lane.
/// </summary>
/// <remarks>
/// Counted on dispatch rather than on receipt, because the question a
/// distribution report answers is what the warehouse sent — and counting on
/// receipt would make a lane look idle while a fortnight of stock sat in a van.
/// What arrived is reported beside it, and the gap between the two is the point.
/// </remarks>
/// <param name="SourceLocationId">Where it left.</param>
/// <param name="SourceCode">That location's code.</param>
/// <param name="SourceName">Its name.</param>
/// <param name="DestinationLocationId">Where it went.</param>
/// <param name="DestinationCode">That location's code.</param>
/// <param name="DestinationName">Its name.</param>
/// <param name="TransferCount">How many transfers were dispatched.</param>
/// <param name="DispatchedQuantity">What left.</param>
/// <param name="ReceivedQuantity">What has been counted at the other end so far.</param>
/// <param name="DamagedQuantity">What arrived damaged.</param>
/// <param name="ShortfallQuantity">What left and has not arrived, damaged or otherwise.</param>
public sealed record DistributionLaneRow(
    Guid SourceLocationId,
    string SourceCode,
    string SourceName,
    Guid DestinationLocationId,
    string DestinationCode,
    string DestinationName,
    int TransferCount,
    decimal DispatchedQuantity,
    decimal ReceivedQuantity,
    decimal DamagedQuantity,
    decimal ShortfallQuantity);
