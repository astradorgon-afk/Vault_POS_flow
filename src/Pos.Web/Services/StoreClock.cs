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

    /// <summary>Converts an instant to the stores' local time for display, so a
    /// sale rung up at 21:57 in Makati reads 21:57 whatever zone the web server
    /// runs in.</summary>
    public static DateTimeOffset InStoreTime(this DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, StoreZone);

    /// <summary>
    /// Resolves a wall-clock time typed on a screen — the receiving form's
    /// "released by supplier" — as the stores' own local time, so the instant
    /// it lands on does not depend on which zone the browser or the server
    /// happens to run in.
    /// </summary>
    /// <param name="localTime">The clock reading as typed, with no offset.</param>
    /// <returns>The instant that reading represents in Asia/Manila.</returns>
    public static DateTimeOffset FromStoreLocal(DateTime localTime)
    {
        DateTime utc = localTime.Kind == DateTimeKind.Utc
            ? localTime
            : TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified),
                StoreZone);

        return new DateTimeOffset(utc);
    }
}
