using Pos.Application.Notifications;
using Pos.Domain.Notifications;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Notifications;

internal static class SyncFailureNotificationFactory
{
    /// <summary>Builds the alert for one uploaded event that needs a person.</summary>
    internal static Notification Create(SyncFailureAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        // A refused event is the urgent one. It sits at the head of that
        // register's queue and everything behind it waits, so a till can be
        // trading all day with nothing reaching head office. An event that was
        // applied and flagged has already landed — the books are right and
        // somebody has to go and look — so it is a warning, not an alarm.
        bool blocking = !string.Equals(alert.Outcome, nameof(SyncOutcome.RequiresReview), StringComparison.Ordinal);

        string title = blocking
            ? $"Register {alert.DeviceCode}: {alert.Type} refused"
            : $"Register {alert.DeviceCode}: {alert.Type} needs review";

        string consequence = blocking
            ? FormattableString.Invariant(
                $"Nothing behind it in that register's queue can reach head office until it is resolved.")
            : "It was applied and flagged; the records are central, and somebody has to check them.";

        return Notification.Create(
            NotificationKind.SyncFailure,
            blocking ? NotificationSeverity.Critical : NotificationSeverity.Warning,
            title,
            Body(alert, consequence),
            FormattableString.Invariant($"sync:failure:{alert.EventId.Value:D}"),
            alert.DecidedAtUtc,
            alert.LocationId);
    }

    /// <summary>
    /// The server's own words, kept, and trimmed only where the column demands it.
    /// </summary>
    /// <remarks>
    /// What the server said is the whole value of the alert — "refused" without
    /// the reason sends somebody to the failure list to find out what they were
    /// already being told.
    /// </remarks>
    private static string Body(SyncFailureAlert alert, string consequence)
    {
        string body = FormattableString.Invariant(
            $"Event {alert.DeviceSequence} from register {alert.DeviceCode}. {consequence}");

        if (alert.Detail is { Length: > 0 } detail)
        {
            body += " Server: " + detail;
        }

        return body.Length > Notification.BodyMaxLength
            ? body[..(Notification.BodyMaxLength - 1)] + "…"
            : body;
    }
}
