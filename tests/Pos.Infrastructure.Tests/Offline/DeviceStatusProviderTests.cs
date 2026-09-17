using FluentAssertions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;
using Pos.Shared.Devices;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// The register's status screen. Two things matter: it tells a cashier the
/// truth about what the device can do, and it says nothing about how the device
/// works.
/// </summary>
public sealed class DeviceStatusProviderTests
{
    private static readonly DateTimeOffset Now = TemporaryDeviceDatabase.Now;

    [Fact]
    public async Task AnUnopenedStore_ReportsNotReady_WithoutThrowing()
    {
        DeviceDatabaseInitializer unopened = new(
            new DeviceDatabaseOptions(Path.Combine(Path.GetTempPath(), "never-opened", "device.db")),
            new ThrowingKeyProvider());

        DeviceStatusProvider provider = new(
            unopened, new AssumeOfflineConnectivityProbe(), new StubClock(Now));

        DeviceStatusView status = await provider.GetAsync(null, CancellationToken.None);

        status.Storage.Should().Be(DeviceStorageState.NotReady);
        status.CanTrade.Should().BeFalse();
        status.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public async Task AnEnrolledDeviceThatHasNeverSynchronised_CannotTrade()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        DeviceStatusView status = await CreateProvider(database).GetAsync(null, CancellationToken.None);

        status.Storage.Should().Be(DeviceStorageState.Ready);
        status.Enrolment.Should().Be(DeviceEnrolmentState.Enrolled);
        status.DeviceCode.Should().Be("D03");
        status.Sync.Should().Be(DeviceSyncState.NeverSynchronised);
        status.LastSynchronisedUtc.Should().BeNull();
        status.CanTrade.Should().BeFalse("a register with no catalogue cannot ring anything up");
    }

    [Fact]
    public async Task AnUnenrolledDevice_SaysSo()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();

        DeviceStatusView status = await CreateProvider(database).GetAsync(null, CancellationToken.None);

        status.Enrolment.Should().Be(DeviceEnrolmentState.NotEnrolled);
        status.DeviceCode.Should().BeNull();
        status.LocationName.Should().BeNull();
        status.CanTrade.Should().BeFalse();
    }

    [Fact]
    public async Task AFullyProvisionedDevice_CanTradeOffline()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        LocationId store = LocationId.New();
        UserId cashier = UserId.New();
        await database.EnrolAsync("D03", store);
        await ProvisionAsync(database, store, cashier, expiresAt: Now.AddHours(40));

        DeviceStatusView status = await CreateProvider(database).GetAsync(cashier, CancellationToken.None);

        status.LocationName.Should().Be("Store 1");
        status.Sync.Should().Be(DeviceSyncState.Synchronised);
        status.LastSynchronisedUtc.Should().Be(Now);
        status.Authority.Should().Be(DeviceAuthorityState.Active);
        status.Connectivity.Should().Be(DeviceConnectivityState.Offline);

        status.CanTrade.Should().BeTrue("selling with no connection is what the device is for");
        status.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public async Task AuthorityInsideTheWarningWindow_AsksToReconnect_ButStillTrades()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        LocationId store = LocationId.New();
        UserId cashier = UserId.New();
        await database.EnrolAsync("D03", store);
        await ProvisionAsync(database, store, cashier, expiresAt: Now.AddHours(40));

        DeviceStatusProvider provider = CreateProvider(database);

        database.Clock.UtcNow = Now.AddHours(40) - DeviceStatusProvider.ExpiringSoonWindow;
        DeviceStatusView warning = await provider.GetAsync(cashier, CancellationToken.None);

        warning.Authority.Should().Be(DeviceAuthorityState.ExpiringSoon);
        warning.CanTrade.Should().BeTrue();
        warning.NeedsAttention.Should().BeTrue("a shift's warning is the point of the window");
    }

    [Fact]
    public async Task ExpiredAuthority_StopsTheRegisterTrading()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        LocationId store = LocationId.New();
        UserId cashier = UserId.New();
        await database.EnrolAsync("D03", store);
        await ProvisionAsync(database, store, cashier, expiresAt: Now.AddHours(40));

        DeviceStatusProvider provider = CreateProvider(database);
        database.Clock.UtcNow = Now.AddHours(40);

        DeviceStatusView status = await provider.GetAsync(cashier, CancellationToken.None);

        status.Authority.Should().Be(DeviceAuthorityState.Expired);
        status.CanTrade.Should().BeFalse();
    }

    [Fact]
    public async Task WithNobodySignedIn_ThereIsNoAuthorityToReport()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        LocationId store = LocationId.New();
        await database.EnrolAsync("D03", store);
        await ProvisionAsync(database, store, UserId.New(), expiresAt: Now.AddHours(40));

        DeviceStatusView status = await CreateProvider(database).GetAsync(null, CancellationToken.None);

        status.Authority.Should().Be(DeviceAuthorityState.None);
        status.AuthorityExpiresUtc.Should().BeNull();
        status.CanTrade.Should().BeFalse("no signed-in cashier, no sale");
    }

    [Fact]
    public async Task TheEarliestExpiryIsReported_BecauseThatIsWhenPermissionsStartGoing()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        LocationId store = LocationId.New();
        UserId cashier = UserId.New();
        await database.EnrolAsync("D03", store);

        await ApplyAsync(database, new ChangeFeedPage(0, 6,
        [
            new LocationChanged(3, store, "STORE-1", "Store 1", Pos.Domain.Locations.LocationKind.Store,
                "Asia/Manila", "PHP", true),
            new PermissionSnapshotIssued(4, cashier, 9, Now, Now.AddHours(40),
                [new PermissionSnapshotGrant(Permissions.Sales.Create, store)]),
            new PermissionSnapshotIssued(6, cashier, 9, Now, Now.AddHours(20),
                [new PermissionSnapshotGrant(Permissions.Sales.Create, store)]),
        ]));

        DeviceStatusView status = await CreateProvider(database).GetAsync(cashier, CancellationToken.None);

        status.AuthorityExpiresUtc.Should().Be(Now.AddHours(20), "the re-issue replaced the longer one");
    }

    [Fact]
    public async Task TheStatus_CarriesNothingAboutHowTheDeviceStoresOrMovesData()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        LocationId store = LocationId.New();
        UserId cashier = UserId.New();
        await database.EnrolAsync("D03", store);
        await ProvisionAsync(database, store, cashier, expiresAt: Now.AddHours(40));

        DeviceStatusView status = await CreateProvider(database).GetAsync(cashier, CancellationToken.None);

        // Everything the view can say, as one string. A cashier photographing
        // this screen for a support ticket must not be handing over the estate's
        // infrastructure.
        string everything = string.Join(
            '|',
            status.DeviceCode,
            status.LocationName,
            status.Storage.ToString(),
            status.Enrolment.ToString(),
            status.Connectivity.ToString(),
            status.Sync.ToString(),
            status.Authority.ToString());

        everything.Should().NotContainAny(
            database.DatabasePath,
            Path.GetDirectoryName(database.DatabasePath)!,
            "device.db",
            "sqlite",
            "SQLCipher",
            "cipher",
            "http",
            "cursor");

        // The identifiers a device holds are its own; none of them belongs on a
        // shop floor screen.
        everything.Should().NotContain(store.Value.ToString());
        everything.Should().NotContain(cashier.Value.ToString());
    }

    private static DeviceStatusProvider CreateProvider(TemporaryDeviceDatabase database)
        => new(database.Initializer, new AssumeOfflineConnectivityProbe(), database.Clock);

    private static async Task ProvisionAsync(
        TemporaryDeviceDatabase database,
        LocationId store,
        UserId cashier,
        DateTimeOffset expiresAt)
        => await ApplyAsync(database, new ChangeFeedPage(0, 6,
        [
            new LocationChanged(3, store, "STORE-1", "Store 1", Pos.Domain.Locations.LocationKind.Store,
                "Asia/Manila", "PHP", true),
            new PermissionSnapshotIssued(6, cashier, 9, Now, expiresAt,
                [new PermissionSnapshotGrant(Permissions.Sales.Create, store)]),
        ]));

    private static async Task ApplyAsync(TemporaryDeviceDatabase database, ChangeFeedPage page)
    {
        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(page);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
    }

    private sealed class ThrowingKeyProvider : IDeviceDatabaseKeyProvider
    {
        public ValueTask<byte[]> GetDatabaseKeyAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("The key must not be read for this test.");
    }

    private sealed class StubClock(DateTimeOffset now) : Pos.Application.Common.Abstractions.ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
