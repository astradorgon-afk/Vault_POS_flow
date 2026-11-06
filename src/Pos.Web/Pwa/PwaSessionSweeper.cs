using Pos.Web.Security;

namespace Pos.Web.Pwa;

/// <summary>
/// Drops sessions and one-use handoff tokens, web and PWA alike, once they expire.
/// </summary>
/// <remarks>
/// Both stores evicted lazily, on the next lookup of the very key that had
/// expired. A device that never comes back is never looked up, so its entry —
/// and the live refresh token inside it — stayed in memory for the life of the
/// process. A handoff token that was issued and never redeemed was never
/// removed at all. This sweep is what bounds both.
/// </remarks>
/// <param name="sessions">The PWA session store.</param>
/// <param name="handoffs">The sign-in handoff store.</param>
/// <param name="webSessions">The signed-in web session store.</param>
/// <param name="pendingSignIns">Sign-ins waiting for the request that sets their cookie.</param>
/// <param name="tickets">Prerender-to-circuit handoff tokens.</param>
/// <param name="logger">The log.</param>
public sealed partial class PwaSessionSweeper(
    PwaSessions sessions,
    PwaLoginHandoffs handoffs,
    WebSessions webSessions,
    OneUseTokens<PendingWebSignIn> pendingSignIns,
    OneUseTokens<WebSessionTicket> tickets,
    ILogger<PwaSessionSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Swept {SessionCount} expired session(s) and {HandoffCount} unused handoff token(s).")]
    private partial void LogSwept(int sessionCount, int handoffCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "The PWA session sweep failed.")]
    private partial void LogSweepFailed(Exception exception);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                int expiredSessions = sessions.RemoveExpired() + webSessions.RemoveExpired();
                int expiredHandoffs = handoffs.RemoveExpired()
                    + pendingSignIns.RemoveExpired()
                    + tickets.RemoveExpired();
                if (expiredSessions > 0 || expiredHandoffs > 0)
                {
                    LogSwept(expiredSessions, expiredHandoffs);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed sweep must not take the host down; the next tick retries.
                LogSweepFailed(exception);
            }
        }
    }
}
