#if DEBUG
using Microsoft.Extensions.Logging;
#endif
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Offline;
using Pos.Application.Sales;
using Pos.Client.Services;
using Pos.Client.Storage;
using Pos.Infrastructure.Common;
using Pos.Infrastructure.Offline;
using Microsoft.Maui.LifecycleEvents;
#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
#endif

namespace Pos.Client;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // The device registers only the whitelisted use cases. Anything else
        // has no handler here, so the dispatcher refuses it rather than
        // running a server use case against a device database.
        builder.Services.AddOfflineClientApplication();

        builder.Services.AddSingleton(new DeviceDatabaseOptions(
            Path.Combine(FileSystem.Current.AppDataDirectory, "device.db")));
        builder.Services.AddSingleton<IDeviceDatabaseKeyProvider, SecureStorageDeviceDatabaseKeyProvider>();
        builder.Services.AddSingleton<DeviceDatabaseInitializer>();
        builder.Services.AddSingleton<ISystemClock, SystemClock>();
        builder.Services.AddSingleton<ChangeFeedApplier>();

        // The device adapters behind the ports the shared handlers resolve:
        // its own document counter instead of the central one, and the cached
        // permission snapshot instead of a live database.
        builder.Services.AddSingleton<IDeviceProfileAccessor, DeviceProfileAccessor>();
        builder.Services.AddSingleton<IPermissionEvaluator, DeviceSnapshotPermissionEvaluator>();
        builder.Services.AddScoped(sp => sp.GetRequiredService<DeviceDatabaseInitializer>().CreateDbContext());
        builder.Services.AddScoped<IDocumentNumberGenerator, DeviceDocumentNumberGenerator>();

        // What the register shows about itself. The probe is here rather than in
        // infrastructure because reachability is a platform question.
        builder.Services.AddSingleton<IDeviceConnectivityProbe, NetworkConnectivityProbe>();
        builder.Services.AddSingleton<DeviceStatusProvider>();

        // Head office: enrolment, sign-in and the store-data download. The
        // register's key pair lives in the platform's secure store.
        builder.Services.AddSingleton(_ => CreateHeadOfficeHttpClient());
        builder.Services.AddSingleton<HeadOfficeClient>();
        builder.Services.AddSingleton(SecureStorage.Default);
        builder.Services.AddSingleton(Preferences.Default);
        builder.Services.AddSingleton<DeviceKeyStore>();

        // Working without head office: sign-in against the password head office
        // last accepted here, the till's view of its open shift, cash sales it
        // queues, and the upload that delivers the queue once connected.
        builder.Services.AddSingleton(sp => new DeviceOfflineSignIn(
            sp.GetRequiredService<DeviceDatabaseInitializer>(),
            sp.GetRequiredService<ISystemClock>()));
        builder.Services.AddSingleton<DeviceShiftMirror>();
        builder.Services.AddSingleton<DeviceOfflineSales>();
        builder.Services.AddSingleton<DeviceOutboxUploader>();
        builder.Services.AddSingleton<RegisterService>();

        // The office console: the same signed-in session drives the head-office
        // HTTP calls, but with a method surface shaped for the back-office pages.
        builder.Services.AddSingleton<BackOfficeService>();

        // What a whitelisted use case executes inside: one session per register,
        // the device's own unit of work, its append-only local audit, and the
        // repositories for the records it keeps until they sync.
        builder.Services.AddSingleton<DeviceSession>();
        builder.Services.AddSingleton<ICurrentUser, DeviceCurrentUser>();
        builder.Services.AddSingleton<INegativeStockAttemptRecorder, DeviceNegativeStockAttemptRecorder>();
        builder.Services.AddScoped<IUnitOfWork, DeviceUnitOfWork>();
        builder.Services.AddScoped<IAuditWriter, DeviceAuditWriter>();
        builder.Services.AddScoped<IDeviceOutbox, DeviceOutbox>();
        builder.Services.AddScoped<IShiftRepository, DeviceShiftRepository>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // The register runs on a dedicated till, so the window fills the
        // screen from launch rather than opening at Windows' default size.
        builder.ConfigureLifecycleEvents(events =>
        {
#if WINDOWS
            events.AddWindows(windows => windows.OnWindowCreated(window =>
            {
                nint handle = WindowNative.GetWindowHandle(window);
                WindowId id = Win32Interop.GetWindowIdFromWindow(handle);
                AppWindow appWindow = AppWindow.GetFromWindowId(id);

                // MAUI applies its own default size right after the window is
                // created, which would undo a Maximize() called this early.
                // Activated fires once the window is actually on screen, after
                // that sizing has already happened.
                void MaximizeOnFirstActivation(object sender, WindowActivatedEventArgs args)
                {
                    window.Activated -= MaximizeOnFirstActivation;
                    if (appWindow.Presenter is OverlappedPresenter presenter)
                    {
                        presenter.Maximize();
                    }
                }

                window.Activated += MaximizeOnFirstActivation;
            }));
#endif
        });

        return builder.Build();
    }

    /// <summary>
    /// The client every head-office call goes through. A head office that is
    /// down should be noticed in seconds, not after the full request timeout,
    /// because the register works offline as soon as it knows; on Windows the
    /// connection attempt itself is bounded for that reason.
    /// </summary>
    private static HttpClient CreateHeadOfficeHttpClient()
    {
#if WINDOWS
        return new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
#else
        return new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
#endif
    }
}
