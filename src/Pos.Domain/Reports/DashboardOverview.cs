using System.Text.Json.Serialization;

namespace Pos.Domain.Reports;

/// <summary>A named period the dashboard understands.</summary>
/// <remarks>
/// Resolved against a <b>timezone</b>, never against UTC. "Today" for a business
/// in Manila is not the UTC day, and a dashboard that showed a store its takings
/// from eight in the morning would be wrong in the way nobody reports because
/// they assume they misread it.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DashboardRange>))]
public enum DashboardRange
{
    /// <summary>The current trading day.</summary>
    Today = 1,

    /// <summary>The trading day before it.</summary>
    Yesterday = 2,

    /// <summary>The last seven trading days, today included.</summary>
    Last7 = 3,

    /// <summary>The last thirty trading days, today included.</summary>
    Last30 = 4,

    /// <summary>The current calendar month to date.</summary>
    Month = 5,

    /// <summary>The whole of the previous calendar month.</summary>
    PrevMonth = 6,

    /// <summary>The current calendar quarter to date.</summary>
    Quarter = 7,

    /// <summary>The current calendar year to date.</summary>
    Year = 8,

    /// <summary>The dates the caller gave.</summary>
    Custom = 9,
}

/// <summary>
/// The headline numbers for a period.
/// </summary>
/// <remarks>
/// Margin and cost are present only for a caller who may see them; for everybody
/// else they are null rather than zero. Zero would read as "we made nothing",
/// which is a statement about the business rather than about the reader.
/// </remarks>
/// <param name="SalesCount">Completed sales.</param>
/// <param name="GrossRevenue">What customers were charged, VAT included, before discount.</param>
/// <param name="Discount">What was taken off.</param>
/// <param name="Revenue">What was earned, net of VAT and after discount.</param>
/// <param name="Cost">The cost of what was sold, or null without the financial permission.</param>
/// <param name="GrossProfit">Revenue less cost, or null without it.</param>
/// <param name="MarginPercent">Gross profit as a percentage of revenue, or null without it.</param>
/// <param name="UnitsSold">Units rung up.</param>
/// <param name="UnitsReturned">How many of them have since come back.</param>
/// <param name="AverageBasket">Revenue divided by sales, or null when nothing sold.</param>
public sealed record DashboardKpis(
    int SalesCount,
    decimal GrossRevenue,
    decimal Discount,
    decimal Revenue,
    decimal? Cost,
    decimal? GrossProfit,
    decimal? MarginPercent,
    decimal UnitsSold,
    decimal UnitsReturned,
    decimal? AverageBasket);

/// <summary>One store's line in the comparison.</summary>
/// <param name="LocationId">The store.</param>
/// <param name="LocationCode">Its code.</param>
/// <param name="LocationName">Its name.</param>
/// <param name="Kpis">Its numbers, on the same basis as the whole.</param>
/// <param name="ShareOfRevenue">Its share of the period's revenue, or null when nothing sold anywhere.</param>
public sealed record DashboardStoreRow(
    Guid LocationId,
    string LocationCode,
    string LocationName,
    DashboardKpis Kpis,
    decimal? ShareOfRevenue);

/// <summary>
/// What the stock looks like right now.
/// </summary>
/// <remarks>
/// A snapshot, not a period: it describes the shelves at the instant the
/// dashboard was asked, whatever range the sales figures cover. Mixing the two
/// silently is how somebody concludes last month's sales emptied a shelf that was
/// restocked on Tuesday.
/// </remarks>
/// <param name="AsOfUtc">When the shelves were read.</param>
/// <param name="Available">Units that may be sold.</param>
/// <param name="InTransit">Units dispatched and not yet received.</param>
/// <param name="Quarantine">Units held pending review.</param>
/// <param name="OtherHeld">Units on hand in every other state — damaged, expired, awaiting inspection.</param>
/// <param name="Value">What all of it is carried at, or null without the financial permission.</param>
/// <param name="LinesOutOfStock">Product-and-location pairs the business stocks and currently has none of.</param>
/// <param name="LinesLow">Pairs at or below their reorder point.</param>
/// <param name="LinesOverstocked">Pairs above their maximum.</param>
public sealed record DashboardInventoryPanel(
    DateTimeOffset AsOfUtc,
    decimal Available,
    decimal InTransit,
    decimal Quarantine,
    decimal OtherHeld,
    decimal? Value,
    int LinesOutOfStock,
    int LinesLow,
    int LinesOverstocked);

/// <summary>The owner's overview.</summary>
/// <param name="Range">The named period asked for.</param>
/// <param name="FromDate">The first business date it covers.</param>
/// <param name="ToDate">The last.</param>
/// <param name="TimeZoneId">The timezone those dates were resolved in.</param>
/// <param name="Kpis">The period's headline numbers.</param>
/// <param name="Stores">One row per store, largest revenue first.</param>
/// <param name="Inventory">What the stock looks like now.</param>
public sealed record DashboardOverview(
    DashboardRange Range,
    DateOnly FromDate,
    DateOnly ToDate,
    string TimeZoneId,
    DashboardKpis Kpis,
    IReadOnlyList<DashboardStoreRow> Stores,
    DashboardInventoryPanel Inventory);
