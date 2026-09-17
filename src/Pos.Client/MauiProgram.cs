using Microsoft.Extensions.Logging;
using Pos.Client.Storage;
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
        builder.Services.AddSingleton(new DeviceDatabaseOptions(
            Path.Combine(FileSystem.Current.AppDataDirectory, "device.db")));
        builder.Services.AddSingleton<IDeviceDatabaseKeyProvider, SecureStorageDeviceDatabaseKeyProvider>();
        builder.Services.AddSingleton<DeviceDatabaseInitializer>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
