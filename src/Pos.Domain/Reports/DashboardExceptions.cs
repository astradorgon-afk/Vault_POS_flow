using System.Text.Json.Serialization;
using Pos.Domain.Notifications;

namespace Pos.Domain.Reports;

/// <summary>A kind of thing that needs somebody.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DashboardExceptionKind>))]
public enum DashboardExceptionKind
{
    /// <summary>Quarantined goods the catalogue does not recognise.</summary>
    UnknownProducts = 1,

    /// <summary>Transfer arrivals that did not match what was dispatched, still unresolved.</summary>
    TransferDiscrepancies = 2,

    /// <summary>Write-offs and corrections above the value threshold.</summary>
    HighValueAdjustments = 3,

    /// <summary>Draws that would have taken a bucket below zero, permitted or refused.</summary>
    NegativeStockAttempts = 4,

    /// <summary>Stock past its expiry date sitting in a bucket a till may sell from.</summary>
    ExpiredStillSellable = 5,

    /// <summary>Products that varied at a count again, having varied at the last one.</summary>
    RepeatedCountVariances = 6,

    /// <summary>Uploaded events head office turned away or set aside.</summary>
    FailedSync = 7,

    /// <summary>Registers that have not been heard from.</summary>
    OfflineDevices = 8,

    /// <summary>Transfers raised under emergency authority, awaiting central review.</summary>
    EmergencyTransfers = 9,
}

/// <summary>One thing on a panel, enough to recognise it and go to it.</summary>
/// <param name="Id">What it is, for the drill-down.</param>
/// <param name="Reference">The document number or code a person would say out loud.</param>
/// <param name="Where">The location's code, where it has one.</param>
/// <param name="AtUtc">When it happened or was raised.</param>
/// <param name="Detail">One line saying what it is.</param>
public sealed record DashboardExceptionItem(
    Guid Id,
    string Reference,
    string Where,
    DateTimeOffset? AtUtc,
    string Detail);

/// <summary>
/// One panel of the exception board.
/// </summary>
/// <remarks>
/// <paramref name="Count"/> is the whole count and <paramref name="Sample"/> is a
/// handful. A panel that said "5" because it had only looked at five would be the
/// worst kind of wrong: it would read as good news.
/// </remarks>
/// <param name="Kind">What kind of exception.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Count">How many there are, in total.</param>
/// <param name="OldestAtUtc">The oldest one, which is usually the one that matters.</param>
/// <param name="Value">What is at stake, or null when there is no money in it or the caller may not see it.</param>
/// <param name="Sample">A few of them, newest-worst first.</param>
public sealed record DashboardExceptionPanel(
    DashboardExceptionKind Kind,
    [property: JsonConverter(typeof(JsonStringEnumConverter<NotificationSeverity>))]
    NotificationSeverity Severity,
    int Count,
    DateTimeOffset? OldestAtUtc,
    decimal? Value,
    IReadOnlyList<DashboardExceptionItem> Sample);

/// <summary>The exception board.</summary>
/// <remarks>
/// Some panels are a standing state — what is unresolved right now — and some
/// count what happened during the period. Both are present, and each panel says
/// which it is by carrying the window it was built over.
/// </remarks>
/// <param name="FromDate">The first business date the period panels cover.</param>
/// <param name="ToDate">The last.</param>
/// <param name="AsOfUtc">When the standing panels were read.</param>
/// <param name="Panels">Every panel, worst first, including the empty ones.</param>
public sealed record DashboardExceptions(
    DateOnly FromDate,
    DateOnly ToDate,
    DateTimeOffset AsOfUtc,
    IReadOnlyList<DashboardExceptionPanel> Panels);
