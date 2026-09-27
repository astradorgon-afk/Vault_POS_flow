using Android.App;
using Android.Content;
using Android.Content.PM;
using Pos.Shared.Scanning;

namespace Pos.Client;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private ScannerBroadcastReceiver? _scanner;

    /// <inheritdoc />
    protected override void OnResume()
    {
        base.OnResume();

        // Listen for the handheld's scanner only while the app is in front.
        if (_scanner is null && IPlatformApplication.Current?.Services.GetService<ScanRouter>() is { } router)
        {
            _scanner = new ScannerBroadcastReceiver(router);

            // Scanner services are other apps, so from Android 13 the receiver
            // must say it accepts broadcasts from outside the app.
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                RegisterReceiver(_scanner, ScannerBroadcastReceiver.CreateFilter(), ReceiverFlags.Exported);
            }
            else
            {
                RegisterReceiver(_scanner, ScannerBroadcastReceiver.CreateFilter());
            }
        }
    }

    /// <inheritdoc />
    protected override void OnPause()
    {
        if (_scanner is not null)
        {
            UnregisterReceiver(_scanner);
            _scanner.Dispose();
            _scanner = null;
        }

        base.OnPause();
    }
}
