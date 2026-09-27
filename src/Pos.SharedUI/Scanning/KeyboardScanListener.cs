using Microsoft.JSInterop;
using Pos.Shared.Scanning;

namespace Pos.SharedUI.Scanning;

/// <summary>
/// Turns keyboard-wedge scanner bursts into scans. The page script groups
/// fast keystrokes; this decides, with <see cref="KeyboardWedge"/>, whether a
/// burst was a scanner, and passes the code to the <see cref="ScanRouter"/>.
/// </summary>
/// <param name="js">The page's JavaScript runtime.</param>
/// <param name="router">Where scans go.</param>
public sealed class KeyboardScanListener(IJSRuntime js, ScanRouter router) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<KeyboardScanListener>? _self;

    /// <summary>Raised with a burst that was judged to be a person typing, for diagnostics.</summary>
    public event Action<string, double[]>? BurstRejected;

    /// <summary>Gets the thresholds in use.</summary>
    public WedgeOptions Options { get; private set; } = WedgeOptions.Default;

    /// <summary>Starts listening; safe to call again, which restarts with new options.</summary>
    /// <param name="options">The thresholds, or the defaults.</param>
    /// <returns>A task that completes once the page is listening.</returns>
    public async Task StartAsync(WedgeOptions? options = null)
    {
        Options = options ?? WedgeOptions.Default;
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/Pos.SharedUI/scanner.js").ConfigureAwait(false);
        _self ??= DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync(
            "start",
            _self,
            new
            {
                minimumLength = Options.MinimumLength,
                maximumAverageGapMilliseconds = Options.MaximumAverageGapMilliseconds,
                maximumGapMilliseconds = Options.MaximumGapMilliseconds,
                idleMilliseconds = Options.IdleMilliseconds,
            }).ConfigureAwait(false);
    }

    /// <summary>Called by the page script with one burst of keystrokes.</summary>
    /// <param name="text">The characters typed.</param>
    /// <param name="times">When each was typed, in milliseconds.</param>
    /// <returns>True when it was a scanner, so the script removes the typed characters.</returns>
    [JSInvokable]
    public bool OnKeyboardBurst(string text, double[] times)
    {
        if (string.IsNullOrEmpty(text) || times is null || times.Length != text.Length)
        {
            return false;
        }

        KeyStroke[] keys = [.. text.Select((c, i) => new KeyStroke(c, times[i]))];
        if (!KeyboardWedge.IsScan(keys, Options))
        {
            BurstRejected?.Invoke(text, times);
            return false;
        }

        string code = BarcodeText.Clean(text);
        if (code.Length == 0)
        {
            return false;
        }

        // Answer the page at once so it can tidy the text box; the screen's
        // own handling (a product lookup, say) carries on behind it.
        _ = router.DispatchAsync(new BarcodeScan(code, ScanSource.Keyboard, DateTimeOffset.UtcNow));
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("stop").ConfigureAwait(false);
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The page is already gone.
            }
            catch (ObjectDisposedException)
            {
                // The runtime is already gone.
            }
        }

        _self?.Dispose();
    }
}
