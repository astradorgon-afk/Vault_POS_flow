namespace Pos.Shared.Scanning;

/// <summary>
/// Sends every scan, from any scanner, to the screen that is waiting for one.
/// </summary>
/// <remarks>
/// Screens claim scans while they are open. The most recent claim wins, so a
/// dialog opened over a list takes the scans and hands them back when it
/// closes. A scan nobody claims is reported so the app can say where to go.
/// Some handhelds deliver one read twice (as keystrokes and as a broadcast);
/// the same code arriving again within <see cref="DuplicateWindow"/> is dropped.
/// </remarks>
/// <param name="clock">The current time; replaceable in tests.</param>
public sealed class ScanRouter(Func<DateTimeOffset>? clock = null)
{
    /// <summary>How close together two identical reads must be to count as one.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(250);

    private const int RecentLimit = 25;

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private readonly List<ClaimHandle> _claims = [];
    private readonly LinkedList<BarcodeScan> _recent = [];
    private BarcodeScan? _last;

    /// <summary>Raised for every accepted scan, claimed or not.</summary>
    public event Action<BarcodeScan>? Scanned;

    /// <summary>Raised for a scan no open screen was waiting for.</summary>
    public event Action<BarcodeScan>? Unclaimed;

    /// <summary>Gets whether any screen is waiting for scans.</summary>
    public bool HasClaim
    {
        get
        {
            lock (_gate)
            {
                return _claims.Count > 0;
            }
        }
    }

    /// <summary>Gets the most recent scans, newest first.</summary>
    public IReadOnlyList<BarcodeScan> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _recent];
            }
        }
    }

    /// <summary>Starts sending scans to a handler until the returned handle is disposed.</summary>
    /// <param name="handler">What to do with each scan.</param>
    /// <returns>Dispose it to stop.</returns>
    public IDisposable Claim(Func<BarcodeScan, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ClaimHandle claim = new(this, handler);
        lock (_gate)
        {
            _claims.Add(claim);
        }

        return claim;
    }

    /// <summary>Delivers a scan to the screen waiting for it.</summary>
    /// <param name="scan">The scan.</param>
    /// <returns>True when a screen took it; false when it was a duplicate or nobody was waiting.</returns>
    public async Task<bool> DispatchAsync(BarcodeScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.Code.Length == 0)
        {
            return false;
        }

        Func<BarcodeScan, Task>? handler;
        lock (_gate)
        {
            DateTimeOffset now = _clock();
            if (_last is { } previous
                && string.Equals(previous.Code, scan.Code, StringComparison.Ordinal)
                && previous.Source != scan.Source
                && now - previous.ReceivedAtUtc < DuplicateWindow)
            {
                return false;
            }

            _last = scan with { ReceivedAtUtc = now };
            _recent.AddFirst(scan);
            while (_recent.Count > RecentLimit)
            {
                _recent.RemoveLast();
            }

            handler = _claims.Count > 0 ? _claims[^1].Handler : null;
        }

        Scanned?.Invoke(scan);
        if (handler is null)
        {
            Unclaimed?.Invoke(scan);
            return false;
        }

        await handler(scan).ConfigureAwait(false);
        return true;
    }

    private void Release(ClaimHandle claim)
    {
        lock (_gate)
        {
            _claims.Remove(claim);
        }
    }

    private sealed class ClaimHandle(ScanRouter owner, Func<BarcodeScan, Task> handler) : IDisposable
    {
        private int _disposed;

        public Func<BarcodeScan, Task> Handler { get; } = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(this);
            }
        }
    }
}
