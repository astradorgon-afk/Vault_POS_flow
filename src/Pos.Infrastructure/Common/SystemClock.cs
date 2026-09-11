using Pos.Application.Common.Abstractions;

namespace Pos.Infrastructure.Common;

/// <summary>
/// The server clock. Authoritative for ordering the ledger, for assigning
/// business dates, and for token and shift windows.
/// </summary>
/// <remarks>
/// Timezones are resolved through <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>
/// with IANA identifiers. On Windows the runtime maps IANA identifiers to
/// Windows zones automatically, so the same configuration value works on a
/// developer machine and in a Linux container.
/// </remarks>
public sealed class SystemClock : ISystemClock
{
    /// <summary>The organization's default timezone, used when a location has none.</summary>
    public const string DefaultTimeZoneId = "Asia/Manila";

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateOnly BusinessDateFor(string timeZoneId)
    {
        TimeZoneInfo zone = ResolveZone(timeZoneId);
        DateTimeOffset local = TimeZoneInfo.ConvertTime(UtcNow, zone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    private static TimeZoneInfo ResolveZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            timeZoneId = DefaultTimeZoneId;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            // A misconfigured location must not stop trading, but the business
            // date it produces would be wrong, so fall back to the organization
            // default rather than to UTC.
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
        }
    }
}
