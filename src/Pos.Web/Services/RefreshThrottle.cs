namespace Pos.Web.Services;

/// <summary>
/// Runs a page refresh a short while after the first request, folding every
/// request in between into that one run. A store selling every second then
/// reloads a page every couple of seconds, not once per sale, and unlike a
/// debounce a steady stream of sales cannot hold the refresh off forever.
/// </summary>
/// <param name="delay">How long to gather requests before refreshing.</param>
/// <param name="refresh">The refresh to run.</param>
public sealed class RefreshThrottle(TimeSpan delay, Func<Task> refresh) : IDisposable
{
    private readonly CancellationTokenSource _stopped = new();
    private int _scheduled;

    /// <summary>Asks for a refresh; ignored while one is already waiting to run.</summary>
    public void Request()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            return;
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(delay, _stopped.Token).ConfigureAwait(false);

            // Clear first, so a change landing during the reload asks again.
            Volatile.Write(ref _scheduled, 0);
            await refresh().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
        catch (ObjectDisposedException)
        {
            // The page closed while the refresh was starting.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopped.Cancel();
        _stopped.Dispose();
    }
}
