using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Application.Common.Abstractions;
using Pos.Application.Inventory;
using Pos.Application.Sales;
using Pos.Infrastructure.Inventory;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Composes the ports a POS device's use cases run against: its encrypted
/// store, its own ledger, its local records and its upload queue.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the alternative was a list of registrations copied by hand
/// into <c>MauiProgram</c>, and the copy fell behind. The device gained sales,
/// voids, reprints, returns and refunds across five chunks; the test host that
/// exercises them grew the repositories they need, and the app's own container
/// did not. Nothing failed, because nothing in the test suite resolved a handler
/// from the app's container — the register would simply have thrown the first
/// time a cashier rang something up.
/// </para>
/// <para>
/// So there is one list, here, and a test resolves every command
/// <c>OfflineCommandCatalogue</c> marks <c>Registered</c> against it.
/// <c>MauiProgram</c> keeps only what a platform can answer and this assembly
/// cannot: where the database file lives, where its key is kept, and whether
/// there is a network.
/// </para>
/// </remarks>
public static class DeviceInfrastructureRegistration
{
    /// <summary>
    /// Adds the device's context, ledger, repositories, outbox and uploader.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// The caller supplies <see cref="DeviceDatabaseOptions"/>,
    /// <see cref="IDeviceDatabaseKeyProvider"/>, <see cref="ISystemClock"/> and
    /// <see cref="IDeviceConnectivityProbe"/>: each is a question only the host
    /// platform can answer.
    /// </remarks>
    public static IServiceCollection AddDeviceInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<DeviceDatabaseInitializer>();
        services.TryAddSingleton<IDeviceProfileAccessor, DeviceProfileAccessor>();
        services.TryAddSingleton<ChangeFeedApplier>();
        services.TryAddSingleton<DeviceSession>();
        services.TryAddSingleton<DeviceStatusProvider>();
        services.TryAddSingleton<ICurrentUser, DeviceCurrentUser>();
        services.TryAddSingleton<IPermissionEvaluator, DeviceSnapshotPermissionEvaluator>();
        services.TryAddSingleton<INegativeStockAttemptRecorder, DeviceNegativeStockAttemptRecorder>();

        // One context per scope, opened from the initializer that holds the key.
        services.TryAddScoped(sp => sp.GetRequiredService<DeviceDatabaseInitializer>().CreateDbContext());

        // The same ledger type the server runs, over the device's store
        // (ADR-0008). A second implementation of double-entry stock would drift.
        services.TryAddScoped<ILedgerStore>(sp => sp.GetRequiredService<PosDeviceDbContext>());
        services.TryAddScoped<ILedgerPolicyProvider, DeviceLedgerPolicyProvider>();
        services.TryAddScoped<IInventoryLedger, InventoryLedger>();

        services.TryAddScoped<IUnitOfWork, DeviceUnitOfWork>();
        services.TryAddScoped<IAuditWriter, DeviceAuditWriter>();
        services.TryAddScoped<IDeviceOutbox, DeviceOutbox>();
        services.TryAddScoped<IDocumentNumberGenerator, DeviceDocumentNumberGenerator>();

        services.TryAddScoped<IShiftRepository, DeviceShiftRepository>();
        services.TryAddScoped<ISalesRepository, DeviceSalesRepository>();
        services.TryAddScoped<ICustomerRepository, DeviceCustomerRepository>();
        services.TryAddScoped<IExpiryService, DeviceExpiryService>();

        // Draining the outbox. The transport is left to the host, which owns the
        // server address and this register's credentials.
        services.TryAddSingleton<SyncRetryPolicy>();
        services.TryAddScoped<SyncUploader>();
        services.TryAddSingleton<ChangeFeedDownloader>();

        return services;
    }
}
