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
        builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        builder.Services.AddSingleton<HeadOfficeClient>();
        builder.Services.AddSingleton(SecureStorage.Default);
        builder.Services.AddSingleton(Preferences.Default);
        builder.Services.AddSingleton<DeviceKeyStore>();
        builder.Services.AddSingleton<RegisterService>();

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

        return builder.Build();
    }
}
