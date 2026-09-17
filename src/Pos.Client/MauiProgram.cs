#if DEBUG
using Microsoft.Extensions.Logging;
#endif
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Offline;
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

        builder.Services.AddSingleton(new DeviceDatabaseOptions(
            Path.Combine(FileSystem.Current.AppDataDirectory, "device.db")));
        builder.Services.AddSingleton<IDeviceDatabaseKeyProvider, SecureStorageDeviceDatabaseKeyProvider>();
        builder.Services.AddSingleton<DeviceDatabaseInitializer>();
        builder.Services.AddSingleton<ISystemClock, SystemClock>();
        builder.Services.AddSingleton<ChangeFeedApplier>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
