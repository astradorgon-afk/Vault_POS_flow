using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Common;
using Pos.Application.Common.Offline;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// The register's own container, checked against the whitelist it is meant to
/// serve.
/// </summary>
/// <remarks>
/// <para>
/// <c>OfflineCommandCatalogue</c> says which use cases a device may run;
/// <c>AddDeviceInfrastructure</c> supplies the ports they run against. Nothing
/// connects the two, and they drifted: the device gained sales, voids,
/// reprints, returns and refunds across five chunks, the test host that
/// exercises them grew the repositories those need, and the app's container was
/// never given them. No test noticed, because every test composed its own
/// container. The register would have thrown the first time a cashier rang
/// something up.
/// </para>
/// <para>
/// So this resolves each registered command's handler from the real
/// composition. It never executes one — that is what the end-to-end device
/// tests are for — it only asks the container to build it, which is exactly the
/// question that went unasked.
/// </para>
/// </remarks>
public sealed class DeviceCompositionTests
{
    [Fact]
    public async Task EveryUseCaseADeviceMayRun_CanBeBuiltFromItsOwnContainer()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        using ServiceProvider provider = Compose(database);
        using IServiceScope scope = provider.CreateScope();

        IReadOnlyList<OfflineCommandDefinition> registered = OfflineCommandCatalogue.Default.Registered;

        registered.Should().NotBeEmpty("a device that can run nothing is not a device");

        foreach (OfflineCommandDefinition command in registered)
        {
            Type handler = typeof(ICommandHandler<,>).MakeGenericType(
                command.CommandType, ResultTypeOf(command.CommandType));

            Action resolving = () => scope.ServiceProvider.GetRequiredService(handler);

            resolving.Should().NotThrow(
                "{0} is whitelisted for offline use, so every port it needs must be registered",
                command.CommandType.Name);
        }
    }

    [Fact]
    public async Task TheUploaderAndItsOutboxResolve()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        using ServiceProvider provider = Compose(database);
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<SyncUploader>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ChangeFeedDownloader>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IDeviceOutbox>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<DeviceStatusProvider>().Should().NotBeNull();
    }

    /// <summary>
    /// The container as <c>MauiProgram</c> composes it, with the four things
    /// only a platform can answer stubbed.
    /// </summary>
    private static ServiceProvider Compose(TemporaryDeviceDatabase database)
    {
        ServiceCollection services = [];

        // The platform's answers first: an opened store, its clock, no network,
        // and a transport that is never called. AddDeviceInfrastructure uses
        // TryAdd throughout, so registering them ahead of it is what lets a host
        // — or this test — substitute one without the list fighting back.
        services.AddSingleton(database.Initializer);
        services.AddSingleton<ISystemClock>(database.Clock);
        services.AddSingleton<IDeviceConnectivityProbe, StubProbe>();
        services.AddSingleton<ISyncTransport, StubTransport>();

        services.AddOfflineClientApplication();
        services.AddDeviceInfrastructure();

        // Validating on build is the point: a missing port fails here rather
        // than at the till, and scope validation catches a singleton that
        // captured something scoped, which on a device would mean one cashier's
        // context serving the next one's shift.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>The <c>TResult</c> of the command's <c>ICommand&lt;T&gt;</c>.</summary>
    private static Type ResultTypeOf(Type commandType)
        => commandType
            .GetInterfaces()
            .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>))
            .GetGenericArguments()[0];

    private sealed class StubProbe : IDeviceConnectivityProbe
    {
        public Pos.Shared.Devices.DeviceConnectivityState Current
            => Pos.Shared.Devices.DeviceConnectivityState.Offline;
    }

    private sealed class StubTransport : ISyncTransport
    {
        public Task<Result<Pos.Shared.Sync.SyncPushResponse>> PushAsync(
            Pos.Shared.Sync.SyncPushRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The composition check never sends anything.");

        public Task<Result<Pos.Shared.Sync.SyncPullResponse>> PullAsync(
            long cursor,
            int limit,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The composition check never asks for anything.");

        public Task<Result<Pos.Shared.Sync.SyncBaselineResponse>> BaselineAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("The composition check never starts a device.");
    }
}
