
namespace Pos.Api.Authorization;

/// <summary>
/// Compile-time generated log messages for token validation.
/// </summary>
/// <remarks>
/// These run on every authenticated request, so the source generator is used to
/// keep the path allocation-free and to skip argument evaluation entirely when
/// the level is disabled.
/// </remarks>
internal static partial class AuthLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Rejected a token for user {UserId}: the account is not active.")]
    public static partial void AccountNotActive(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Rejected a stale token for user {UserId}: the security stamp has changed.")]
    public static partial void SecurityStampChanged(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Rejected a token bound to device {DeviceId}, which is {Status}.")]
    public static partial void DeviceNotOperational(ILogger logger, Guid deviceId, string status);
}
