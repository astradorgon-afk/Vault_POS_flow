using Microsoft.Extensions.Logging;

namespace Pos.Application.Common.Behaviours;

/// <summary>
/// Compile-time generated log messages for the pipeline. Using the source
/// generator keeps the hot path allocation-free and guarantees that argument
/// evaluation is skipped when the level is disabled.
/// </summary>
internal static partial class BehaviourLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "{Message} succeeded in {ElapsedMs:F1} ms.")]
    public static partial void Succeeded(ILogger logger, string message, double elapsedMs);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "{Message} failed in {ElapsedMs:F1} ms with {ErrorCode}.")]
    public static partial void Failed(ILogger logger, string message, double elapsedMs, string errorCode);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "{Message} threw after {ElapsedMs:F1} ms.")]
    public static partial void Threw(ILogger logger, Exception exception, string message, double elapsedMs);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Permission {Permission} denied for user {UserId} at location {LocationId} on {Message}.")]
    public static partial void PermissionDenied(
        ILogger logger,
        string permission,
        Guid userId,
        Guid? locationId,
        string message);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Error,
        Message = "Refused stock draws from {Message} could not be recorded.")]
    public static partial void NegativeStockAttemptsNotRecorded(ILogger logger, Exception exception, string message);
}
