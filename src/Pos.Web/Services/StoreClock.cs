namespace Pos.Web.Services;

/// <summary>
/// The stores' calendar. Sales are filed under the business date where the
/// store trades (Asia/Manila), so every page that picks "today" or "the last
/// 7 days" uses this clock rather than the web server's, and all of them agree
/// with the registers and with each other.
/// </summary>
public static class StoreClock
{
    private static readonly TimeZoneInfo StoreZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

    /// <summary>Gets the current time where the stores trade.</summary>
    public static DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, StoreZone);

    /// <summary>Gets the stores' current business date.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    /// <summary>Converts an instant to the stores' local time for display.</summary>
    public static DateTimeOffset ToStoreTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, StoreZone);
}
