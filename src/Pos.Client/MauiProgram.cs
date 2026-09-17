#if DEBUG
using Microsoft.Extensions.Logging;
#endif
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Offline;
using Pos.Application.Sales;
using Pos.Client.Storage;
using Pos.Infrastructure.Common;
using Pos.Infrastructure.Offline;

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

        // What only the platform can answer: where the store lives, where its
        // key is kept, what time it is, and whether there is a network.
        builder.Services.AddSingleton(new DeviceDatabaseOptions(
            Path.Combine(FileSystem.Current.AppDataDirectory, "device.db")));
        builder.Services.AddSingleton<IDeviceDatabaseKeyProvider, SecureStorageDeviceDatabaseKeyProvider>();
        builder.Services.AddSingleton<ISystemClock, SystemClock>();
        builder.Services.AddSingleton<IDeviceConnectivityProbe, NetworkConnectivityProbe>();

        // Everything the device's use cases run against: its store, its ledger,
        // its local records, its outbox and its uploader. One list, in
        // AddDeviceInfrastructure, and a test resolves every whitelisted handler
        // against it — this file used to hold a hand-copied one that fell behind
        // by five chunks' worth of repositories.
        builder.Services.AddDeviceInfrastructure();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
