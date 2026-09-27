using Android.Content;
using Android.OS;
using Pos.Shared.Scanning;

namespace Pos.Client;

/// <summary>
/// Receives the broadcast an Android handheld sends when its scan trigger is
/// pressed, and passes the code to the open screen.
/// </summary>
/// <remarks>
/// Registered only while the app is in front (see <see cref="MainActivity"/>),
/// so a scan made in another app never reaches the till.
/// </remarks>
/// <param name="router">Where scans go.</param>
public sealed class ScannerBroadcastReceiver(ScanRouter router) : BroadcastReceiver
{
    /// <summary>Gets the filter for every scanner broadcast the app understands.</summary>
    /// <returns>A new filter.</returns>
    public static IntentFilter CreateFilter()
    {
        IntentFilter filter = new();
        foreach (string action in ScanIntentProfiles.Actions)
        {
            filter.AddAction(action);
        }

        // Makers such as Datalogic send their scan with a category attached;
        // an intent carrying a category only matches a filter that lists it.
        filter.AddCategory(Intent.CategoryDefault);
        filter.AddCategory("com.datalogic.decodewedge.decode_category");
        return filter;
    }

    /// <inheritdoc />
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action is not { } action)
        {
            return;
        }

        BarcodeScan? scan = ScanIntentParser.Parse(action, ReadExtras(intent.Extras), DateTimeOffset.UtcNow);
        Android.Util.Log.Info("VaultFlowScanner", scan is null
            ? $"Ignored {action}: no code in it."
            : $"Scan from {scan.Vendor}: {scan.Code}");
        if (scan is not null)
        {
            _ = router.DispatchAsync(scan);
        }
    }

    // Extras arrive as Java objects; turn them into the plain values the parser reads.
    private static Dictionary<string, object?> ReadExtras(Bundle? bundle)
    {
        Dictionary<string, object?> extras = new(StringComparer.Ordinal);
        if (bundle?.KeySet() is not { } keys)
        {
            return extras;
        }

        foreach (string key in keys)
        {
#pragma warning disable CS0618, CA1422 // Bundle.Get is the only call that reads an extra of unknown type on every Android version.
            Java.Lang.Object? raw = bundle.Get(key);
#pragma warning restore CS0618, CA1422
            extras[key] = raw switch
            {
                null => null,
                Java.Lang.String text => text.ToString(),
                Java.Lang.Integer number => number.IntValue(),
                Java.Lang.Long number => number.LongValue(),
                Java.Lang.Short number => (int)number.ShortValue(),
                Java.Lang.Byte number => (int)number.ByteValue(),
                _ when bundle.GetByteArray(key) is { } bytes => bytes,
                _ => raw.ToString(),
            };
        }

        return extras;
    }
}
