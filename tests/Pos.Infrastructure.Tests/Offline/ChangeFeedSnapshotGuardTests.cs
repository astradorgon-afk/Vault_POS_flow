using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// What the device refuses to cache. The feed arrives over the network, so a
/// page that would widen a device's authority — or wind it back to a policy the
/// server has since narrowed — is refused whole, before any of it is stored.
/// </summary>
public sealed class ChangeFeedSnapshotGuardTests
{
    private static readonly DateTimeOffset Issued = TemporaryDeviceDatabase.Now;

    [Fact]
    public async Task ASnapshotCarryingApprovalAuthority_IsRefusedWhole()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId manager = UserId.New();
        LocationId store = LocationId.New();

        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(new ChangeFeedPage(0, 5,
        [
            Snapshot(5, manager, 12,
                (Permissions.Sales.Create, store),
                (Permissions.Inventory.ApproveAdjustment, store)),
        ]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sync.feed_page_invalid");
        result.Error.Message.Should().Contain("may not hold offline");

        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.PermissionSnapshots.AnyAsync(CancellationToken.None))
            .Should().BeFalse("the offline-capable grant in the same page is refused with it");
        (await context.SyncCursors.AnyAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ASnapshotNamingAPermissionThisClientDoesNotKnow_IsRefused()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();

        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(new ChangeFeedPage(0, 5,
        [
            Snapshot(5, UserId.New(), 12, ("sale.invented", null)),
        ]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sync.feed_page_invalid");
        result.Error.Message.Should().Contain("does not know");
    }

    [Fact]
    public async Task AnOlderPolicyVersion_IsRefused_AndLeavesTheHeldSnapshotIntact()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId cashier = UserId.New();
        LocationId store = LocationId.New();

        // The server narrowed this cashier: version 12 granted void, version 13
        // took it away.
        await ApplySuccessfullyAsync(database, new ChangeFeedPage(0, 5,
        [
            Snapshot(5, cashier, 12, (Permissions.Sales.Create, store), (Permissions.Sales.Void, store)),
        ]));
        await ApplySuccessfullyAsync(database, new ChangeFeedPage(5, 9,
        [
            Snapshot(9, cashier, 13, (Permissions.Sales.Create, store)),
        ]));

        // Replaying the older page would hand the void back.
        Result<ChangeFeedApplyOutcome> replayed = await database.Applier.ApplyAsync(new ChangeFeedPage(9, 14,
        [
            Snapshot(14, cashier, 12, (Permissions.Sales.Create, store), (Permissions.Sales.Void, store)),
        ]));

        replayed.IsFailure.Should().BeTrue();
        replayed.Error.Code.Should().Be("sync.snapshot_policy_rollback");
        replayed.Error.Type.Should().Be(ErrorType.Conflict);

        (await database.Applier.GetCursorAsync()).Should().Be(9, "a refused page does not move the cursor");

        DeviceSnapshotPermissionEvaluator evaluator = database.CreateEvaluator();
        (await evaluator.HasPermissionAsync(cashier, Permissions.Sales.Void, store, CancellationToken.None))
            .Should().BeFalse("the narrowing stands");
        (await evaluator.HasPermissionAsync(cashier, Permissions.Sales.Create, store, CancellationToken.None))
            .Should().BeTrue("the snapshot it already held is untouched");
    }

    [Fact]
    public async Task TheSamePolicyVersion_IsAcceptedAsARefreshedExpiry()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId cashier = UserId.New();
        LocationId store = LocationId.New();

        await ApplySuccessfullyAsync(database, new ChangeFeedPage(0, 5,
        [
            Snapshot(5, cashier, 12, (Permissions.Sales.Create, store)),
        ]));

        await ApplySuccessfullyAsync(database, new ChangeFeedPage(5, 9,
        [
            new PermissionSnapshotIssued(
                9, cashier, 12, Issued.AddHours(6), Issued.AddHours(30),
                [new PermissionSnapshotGrant(Permissions.Sales.Create, store)]),
        ]));

        await using PosDeviceDbContext context = await database.OpenContextAsync();
        DevicePermissionSnapshot held = await context.PermissionSnapshots.SingleAsync(CancellationToken.None);

        held.ExpiresAtUtc.Should().Be(Issued.AddHours(30), "an unchanged policy may still extend the expiry");
    }

    [Fact]
    public async Task ARefusedPageRollsBackTheChangesBeforeIt()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId cashier = UserId.New();
        LocationId store = LocationId.New();
        ProductId productId = ProductId.New();

        await ApplySuccessfullyAsync(database, new ChangeFeedPage(0, 5,
        [
            Snapshot(5, cashier, 13, (Permissions.Sales.Create, store)),
        ]));

        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(new ChangeFeedPage(5, 12,
        [
            new ProductChanged(7, productId, "SKU-9", "Sardines", true, false, false, 1, Issued),
            Snapshot(11, cashier, 12, (Permissions.Sales.Create, store)),
        ]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sync.snapshot_policy_rollback");

        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.AnyAsync(CancellationToken.None))
            .Should().BeFalse("the product written earlier in the page goes back with it");
        (await database.Applier.GetCursorAsync()).Should().Be(5);
    }

    private static PermissionSnapshotIssued Snapshot(
        long sequence,
        UserId userId,
        long policyVersion,
        params (string Permission, LocationId? LocationId)[] grants)
        => new(
            sequence,
            userId,
            policyVersion,
            Issued,
            Issued.AddHours(12),
            [.. grants.Select(g => new PermissionSnapshotGrant(g.Permission, g.LocationId))]);

    private static async Task ApplySuccessfullyAsync(TemporaryDeviceDatabase database, ChangeFeedPage page)
    {
        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(page);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
    }
}
