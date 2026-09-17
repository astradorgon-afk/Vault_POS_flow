using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// Offline authorization. A cached snapshot is an upper bound the server
/// re-checks at sync time (PERMISSIONS.md §5), so every one of these asks the
/// same question: can a device answer "yes" to something the server would not?
/// </summary>
public sealed class DeviceSnapshotPermissionEvaluatorTests
{
    private static readonly DateTimeOffset Issued = TemporaryDeviceDatabase.Now;

    [Fact]
    public async Task AGrantedPermission_IsHeldUntilItExpires()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId cashier = UserId.New();
        LocationId store = LocationId.New();
        await IssueAsync(database, cashier, policyVersion: 7, (Permissions.Sales.Create, store));

        DeviceSnapshotPermissionEvaluator evaluator = database.CreateEvaluator();

        (await evaluator.HasPermissionAsync(cashier, Permissions.Sales.Create, store, CancellationToken.None))
            .Should().BeTrue();

        // One second past expiry, the same question gets the opposite answer.
        database.Clock.UtcNow = Issued.AddHours(12).AddSeconds(1);

        (await evaluator.HasPermissionAsync(cashier, Permissions.Sales.Create, store, CancellationToken.None))
            .Should().BeFalse("a device left in a drawer must not keep its authority");
    }

    [Fact]
    public async Task ALocationGrant_DoesNotReachAnotherLocation()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId cashier = UserId.New();
        LocationId ownStore = LocationId.New();
        LocationId otherStore = LocationId.New();
        await IssueAsync(database, cashier, policyVersion: 7, (Permissions.Sales.Void, ownStore));

        DeviceSnapshotPermissionEvaluator evaluator = database.CreateEvaluator();

        (await evaluator.HasPermissionAsync(cashier, Permissions.Sales.Void, ownStore, CancellationToken.None))
            .Should().BeTrue();
        (await evaluator.HasPermissionAsync(cashier, Permissions.Sales.Void, otherStore, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task AGlobalGrant_AnswersAtAnyLocation()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId manager = UserId.New();
        await IssueAsync(database, manager, policyVersion: 7, (Permissions.Catalog.View, null));

        DeviceSnapshotPermissionEvaluator evaluator = database.CreateEvaluator();

        (await evaluator.HasPermissionAsync(manager, Permissions.Catalog.View, LocationId.New(), CancellationToken.None))
            .Should().BeTrue();
        (await evaluator.HasPermissionAsync(manager, Permissions.Catalog.View, null, CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task APermissionNobodyWasGranted_IsRefused()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId cashier = UserId.New();
        LocationId store = LocationId.New();
        await IssueAsync(database, cashier, policyVersion: 7, (Permissions.Sales.Create, store));

        DeviceSnapshotPermissionEvaluator evaluator = database.CreateEvaluator();

        (await evaluator.HasPermissionAsync(cashier, Permissions.Sales.Void, store, CancellationToken.None))
            .Should().BeFalse();
        (await evaluator.HasPermissionAsync(UserId.New(), Permissions.Sales.Create, store, CancellationToken.None))
            .Should().BeFalse("a user with no snapshot holds nothing");
    }

    [Fact]
    public async Task ATamperedRow_NamingAPermissionADeviceMayNotHold_IsStillRefused()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId manager = UserId.New();
        LocationId store = LocationId.New();
        await IssueAsync(database, manager, policyVersion: 7, (Permissions.Sales.Create, store));

        // The feed refuses such a grant and the table's triggers refuse any
        // writer but the applier, so the only way this row can exist is the one
        // the guards were never meant to cover: someone holding the database key
        // who drops the triggers first (OFFLINE_SYNC.md §2). The evaluator is
        // what stands behind that.
        await TamperAsync(database, Permissions.Inventory.ApproveAdjustment);

        DeviceSnapshotPermissionEvaluator evaluator = database.CreateEvaluator();

        (await evaluator.HasPermissionAsync(
                manager, Permissions.Inventory.ApproveAdjustment, store, CancellationToken.None))
            .Should().BeFalse("approval authority is not offline-capable, whatever a stored row says");

        (await evaluator.GetEffectivePermissionsAsync(manager, CancellationToken.None))
            .Should().BeEmpty("the effective set is filtered by the catalogue too, not just the row");
    }

    [Fact]
    public async Task AnUnknownPermissionCode_IsRefused()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId cashier = UserId.New();

        DeviceSnapshotPermissionEvaluator evaluator = database.CreateEvaluator();

        (await evaluator.HasPermissionAsync(cashier, "sale.invented", null, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TheEffectiveSet_IsWhatIsCachedAndUnexpired()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId cashier = UserId.New();
        LocationId store = LocationId.New();
        await IssueAsync(
            database,
            cashier,
            policyVersion: 7,
            (Permissions.Sales.Create, store),
            (Permissions.Sales.Reprint, store),
            (Permissions.Catalog.View, null));

        DeviceSnapshotPermissionEvaluator evaluator = database.CreateEvaluator();

        (await evaluator.GetEffectivePermissionsAsync(cashier, CancellationToken.None))
            .Should().BeEquivalentTo([Permissions.Sales.Create, Permissions.Sales.Reprint, Permissions.Catalog.View]);

        database.Clock.UtcNow = Issued.AddHours(12).AddSeconds(1);

        (await evaluator.GetEffectivePermissionsAsync(cashier, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARevokedSnapshot_LeavesNothingBehind()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId cashier = UserId.New();
        LocationId store = LocationId.New();
        await IssueAsync(database, cashier, policyVersion: 7, (Permissions.Sales.Create, store));

        Result<ChangeFeedApplyOutcome> revoked = await database.Applier.ApplyAsync(
            new ChangeFeedPage(5, 6, [new PermissionSnapshotRevoked(6, cashier)]));

        revoked.IsSuccess.Should().BeTrue();

        (await database.CreateEvaluator()
                .HasPermissionAsync(cashier, Permissions.Sales.Create, store, CancellationToken.None))
            .Should().BeFalse();
    }

    private static async Task IssueAsync(
        TemporaryDeviceDatabase database,
        UserId userId,
        long policyVersion,
        params (string Permission, LocationId? LocationId)[] grants)
    {
        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(new ChangeFeedPage(0, 5,
        [
            new PermissionSnapshotIssued(
                5, userId, policyVersion, Issued, Issued.AddHours(12),
                [.. grants.Select(g => new PermissionSnapshotGrant(g.Permission, g.LocationId))]),
        ]));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
    }

    /// <summary>
    /// Rewrites a stored grant the way only a key-holder could: on a raw keyed
    /// connection, with the table's guard triggers dropped first.
    /// </summary>
    private static async Task TamperAsync(TemporaryDeviceDatabase database, string permission)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection =
            new(database.EncryptedConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using (Microsoft.Data.Sqlite.SqliteCommand drop = connection.CreateCommand())
        {
            drop.CommandText =
                "DROP TRIGGER IF EXISTS trg_snapshot_permission_feed_only_insert;" +
                "DROP TRIGGER IF EXISTS trg_snapshot_permission_feed_only_update;" +
                "DROP TRIGGER IF EXISTS trg_snapshot_permission_feed_only_delete;";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using Microsoft.Data.Sqlite.SqliteCommand update = connection.CreateCommand();
        update.CommandText = "UPDATE snapshot_permission SET permission = $permission";
        update.Parameters.AddWithValue("$permission", permission);

        (await update.ExecuteNonQueryAsync(CancellationToken.None))
            .Should().Be(1, "the snapshot holds exactly the one grant this test issued");
    }
}
