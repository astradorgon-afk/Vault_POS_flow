namespace Pos.Infrastructure.Offline;

/// <summary>
/// How long a device waits before trying an unsent event again
/// (OFFLINE_SYNC.md §3.2).
/// </summary>
/// <remarks>
/// <para>
/// The delay doubles from five seconds and stops at thirty minutes, so a shop
/// whose line comes back finds out within half an hour rather than at the end of
/// an ever-growing wait. It is multiplied by a random factor between 0.8 and
/// 1.2, which matters when the reason every register is failing is the same
/// outage: without it they would all come back in lockstep and hit the server
/// together, in a thundering herd of exactly the shape that caused the outage.
/// </para>
/// <para>
/// After <see cref="MaxAttempts"/> consecutive failures the event stops being
/// retried and is escalated. It is never dropped — the whole point is that the
/// thing that could not be sent is the thing somebody has to see.
/// </para>
/// </remarks>
/// <param name="random">The jitter source; deterministic in tests.</param>
public sealed class SyncRetryPolicy(Random? random = null)
{
    /// <summary>The first delay, doubled on each subsequent attempt.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);

    /// <summary>The longest a device waits between attempts.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(30);

    private readonly Random _random = random ?? Random.Shared;

    /// <summary>Consecutive failures after which an event is escalated rather than retried.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>The wait before the next attempt.</summary>
    /// <param name="attemptCount">How many attempts have already been made.</param>
    /// <returns>The delay, jittered.</returns>
    public TimeSpan DelayFor(int attemptCount)
    {
        int exponent = Math.Clamp(attemptCount - 1, 0, 20);
        double seconds = BaseDelay.TotalSeconds * Math.Pow(2, exponent);
        double capped = Math.Min(seconds, MaxDelay.TotalSeconds);

        // Jitter is applied after the cap, so the ceiling is a target rather than
        // a hard limit and two registers still separate at the ceiling.
        double jitter = 0.8 + (this._random.NextDouble() * 0.4);

        return TimeSpan.FromSeconds(capped * jitter);
    }
}
