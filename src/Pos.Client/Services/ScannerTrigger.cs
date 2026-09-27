#if ANDROID
using Pos.Shared.Scanning;
#endif

namespace Pos.Client.Services;

/// <summary>
/// Fires a handheld's built-in scanner from an on-screen button, for when the
/// physical trigger is awkward to reach. Android only; a desktop scanner has
/// its own trigger.
/// </summary>
public static class ScannerTrigger
{
    /// <summary>Gets whether this device can have its scanner fired from the screen.</summary>
    public static bool IsAvailable => OperatingSystem.IsAndroid();

    /// <summary>Asks the handheld's scanner to read. Makers that do not support it ignore the request.</summary>
    public static void Fire()
    {
#if ANDROID
        if (Platform.CurrentActivity is not { } activity)
        {
            return;
        }

        foreach (SoftTriggerIntent trigger in ScanIntentProfiles.SoftTriggers)
        {
            using Android.Content.Intent intent = new(trigger.Action);
            foreach ((string key, string value) in trigger.Extras)
            {
                intent.PutExtra(key, value);
            }

            activity.SendBroadcast(intent);
        }
#endif
    }
}
