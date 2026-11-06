using System.Globalization;

namespace Pos.Client.Services;

/// <summary>How stock figures read on the office screens.</summary>
public static class StockText
{
    /// <summary>The statuses in the order a stock-keeper deals with them.</summary>
    public static readonly string[] Statuses = ["Out", "Low", "Healthy", "Over"];

    /// <summary>Gets the plain-language name of a stock status.</summary>
    public static string Label(string status) => status switch
    {
        "Out" => "Out of stock",
        "Low" => "Low",
        "Healthy" => "In stock",
        "Over" => "Overstocked",
        _ => status,
    };

    /// <summary>Gets the colour family for a stock status.</summary>
    public static string Tone(string status) => status switch
    {
        "Out" => "bad",
        "Low" => "warn",
        "Over" => "info",
        _ => "ok",
    };

    /// <summary>Gets how urgent a status is, most urgent first.</summary>
    public static int Rank(string status) => Array.IndexOf(Statuses, status) is var i and >= 0 ? i : Statuses.Length;

    /// <summary>Formats a quantity without trailing zeros.</summary>
    public static string Quantity(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);

    /// <summary>Formats an amount in pesos.</summary>
    public static string Money(decimal value) => value.ToString("₱#,0", CultureInfo.InvariantCulture);

    /// <summary>How long without a sale before stock counts as not selling.</summary>
    public const int IdleDays = 30;

    /// <summary>
    /// Gets whether a product has plenty on hand but has not sold here for at
    /// least <see cref="IdleDays"/> days: money and shelf space standing still.
    /// </summary>
    public static bool IsIdle(PosStockLevel row, DateOnly today)
        => row.Available > 0
            && row.Status is "Healthy" or "Over"
            && (row.LastSoldOn is not { } sold || today.DayNumber - sold.DayNumber >= IdleDays);

    /// <summary>Says when a product last sold, in words.</summary>
    public static string LastSold(PosStockLevel row, DateOnly today) => row.LastSoldOn is { } sold
        ? (today.DayNumber - sold.DayNumber) switch
        {
            <= 0 => "sold today",
            1 => "last sold yesterday",
            int days => $"last sold {days} days ago",
        }
        : "never sold here";

    /// <summary>Gets the store's business date on this device.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Says how long the stock lasts, in words.</summary>
    public static string Cover(PosStockLevel row) => row.Available <= 0 ? "none left"
        : row.DaysOfCover is { } days ? (days < 1 ? "under a day" : $"{days.ToString("0", CultureInfo.InvariantCulture)} day{(days >= 1.5m ? "s" : "")}")
        : "not selling";
}
