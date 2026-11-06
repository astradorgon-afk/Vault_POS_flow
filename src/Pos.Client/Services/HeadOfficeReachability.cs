namespace Pos.Client.Services;

/// <summary>
/// What the register last learned about head office from actually calling it.
/// </summary>
/// <remarks>
/// The platform's network flag only knows about the link. A shop's Wi-Fi can be
/// up while the API is down, and the register must then say it is offline
/// rather than promise sales that will fail. Every head-office call reports
/// here, so the status banner reflects the server, not just the cable.
/// </remarks>
public sealed class HeadOfficeReachability
{
    private readonly Lock _gate = new();
    private bool _lastCallFailed;
    private DateTimeOffset? _lastReportUtc;

    /// <summary>Raised when head office goes from reachable to unreachable or back.</summary>
    public event Action? Changed;

    /// <summary>Gets a value indicating whether the last call to head office failed to get an answer.</summary>
    public bool LastCallFailed
    {
        get
        {
            lock (_gate)
            {
                return _lastCallFailed;
            }
        }
    }

    /// <summary>Gets when head office was last called.</summary>
    public DateTimeOffset? LastReportUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastReportUtc;
            }
        }
    }

    /// <summary>Records the outcome of one call.</summary>
    /// <param name="reachable">Whether head office answered.</param>
    public void Report(bool reachable)
    {
        bool changed;
        lock (_gate)
        {
            changed = _lastCallFailed == reachable;
            _lastCallFailed = !reachable;
            _lastReportUtc = DateTimeOffset.UtcNow;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
