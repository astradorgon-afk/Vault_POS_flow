using Pos.Shared.Scanning;

namespace Pos.Web.Services;

/// <summary>
/// Handles scans one at a time, in the order they arrived. A screen that looks
/// each scan up at head office would otherwise drop the scans that arrive while
/// a lookup is still running.
/// </summary>
/// <param name="process">What to do with each scan.</param>
public sealed class ScanLine(Func<BarcodeScan, Task> process)
{
    private readonly Queue<BarcodeScan> _pending = new();
    private bool _running;

    /// <summary>Queues a scan and works through the queue if it is not already being worked.</summary>
    /// <param name="scan">The scan.</param>
    /// <returns>A task that completes when this call has nothing more to do.</returns>
    public async Task EnqueueAsync(BarcodeScan scan)
    {
        _pending.Enqueue(scan);
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            while (_pending.TryDequeue(out BarcodeScan? next))
            {
                await process(next);
            }
        }
        finally
        {
            _running = false;
        }
    }
}
