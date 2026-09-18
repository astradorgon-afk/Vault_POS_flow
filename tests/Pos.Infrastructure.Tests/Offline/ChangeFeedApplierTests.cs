using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

public sealed class ChangeFeedApplierTests
{
    private static readonly DateTimeOffset IssuedAt = TemporaryDeviceDatabase.Now;

    [Fact]
    public async Task ApplyAsync_WritesEveryChangeKind_AndAdvancesTheCursorWithThePage()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        ProductId productId = ProductId.New();
        LocationId storeId = LocationId.New();
        UserId userId = UserId.New();

        ChangeFeedPage page = new(0, 12,
        [
            Product(3, productId, "SKU-1", "Rice 5kg"),
            new ProductBarcodeChanged(4, "4800000000017", productId, IsPrimary: true, IsActive: true),
            new ProductPriceChanged(5, ProductPriceId.New(), productId, storeId, 245.5m, "PHP", IssuedAt, null),
            new LocationChanged(7, storeId, "STORE-1", "Store 1", LocationKind.Store, "Asia/Manila", "PHP", true),
            new UserChanged(8, userId, "maria", "Maria S.", true, 3),
            Snapshot(9, userId, 41, ("sale.create", storeId), ("product.view", null)),
        ]);

        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(page);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new ChangeFeedApplyOutcome(12, 6, AlreadyApplied: false));
        (await database.Applier.GetCursorAsync()).Should().Be(12);

        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.SingleAsync()).Name.Should().Be("Rice 5kg");
        (await context.ProductBarcodes.SingleAsync()).ProductId.Should().Be(productId);
        DeviceCachedProductPrice price = await context.ProductPrices.SingleAsync();
        price.Amount.Should().Be(245.5m);
        price.LocationId.Should().Be(storeId);
        (await context.Locations.SingleAsync()).Kind.Should().Be(LocationKind.Store);
        (await context.Users.SingleAsync()).SecurityVersion.Should().Be(3);

        List<DevicePermissionSnapshot> grants = await context.PermissionSnapshots.ToListAsync();
        grants.Select(g => (g.Permission, g.LocationId)).Should().BeEquivalentTo(
            [("sale.create", (LocationId?)storeId), ("product.view", (LocationId?)null)]);
        grants.Should().OnlyContain(g => g.PolicyVersion == 41 && g.ExpiresAtUtc == IssuedAt.AddHours(12));

        (await context.SyncCursors.SingleAsync()).AdvancedAtUtc.Should().Be(TemporaryDeviceDatabase.Now);
    }

    [Fact]
    public async Task ApplyAsync_LaterPages_UpdateRows_RemoveCancelledPrices_AndReplaceSnapshots()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        ProductId productId = ProductId.New();
        ProductPriceId priceId = ProductPriceId.New();
        UserId userId = UserId.New();

        await ApplySuccessfullyAsync(database, new ChangeFeedPage(0, 5,
        [
            Product(1, productId, "SKU-1", "Rice 5kg"),
            new ProductPriceChanged(2, priceId, productId, null, 250m, "PHP", IssuedAt.AddDays(3), null),
            Snapshot(3, userId, 1, ("sale.create", null), ("sale.void", null)),
        ]));

        await ApplySuccessfullyAsync(database, new ChangeFeedPage(5, 9,
        [
            Product(6, productId, "SKU-1", "Rice 5kg (discontinued)", isActive: false),
            new ProductPriceRemoved(7, priceId),
            Snapshot(8, userId, 2, ("sale.create", null)),
        ]));

        await using (PosDeviceDbContext context = await database.OpenContextAsync())
        {
            DeviceCachedProduct product = await context.Products.SingleAsync();
            product.Name.Should().Be("Rice 5kg (discontinued)");
            product.IsActive.Should().BeFalse();
            (await context.ProductPrices.AnyAsync()).Should().BeFalse();
            DevicePermissionSnapshot grant = await context.PermissionSnapshots.SingleAsync();
            grant.Permission.Should().Be("sale.create");
            grant.PolicyVersion.Should().Be(2);
        }

        await ApplySuccessfullyAsync(database, new ChangeFeedPage(9, 10, [new PermissionSnapshotRevoked(10, userId)]));

        await using PosDeviceDbContext after = await database.OpenContextAsync();
        (await after.PermissionSnapshots.AnyAsync()).Should().BeFalse();
        (await database.Applier.GetCursorAsync()).Should().Be(10);
    }

    [Fact]
    public async Task ApplyAsync_SeveralChangesToOneRowInAPage_ApplyInFeedOrder()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        ProductId productId = ProductId.New();
        UserId userId = UserId.New();

        await ApplySuccessfullyAsync(database, new ChangeFeedPage(0, 4,
        [
            Product(1, productId, "SKU-1", "First name"),
            Snapshot(2, userId, 1, ("sale.create", null)),
            Product(3, productId, "SKU-1", "Second name"),
            new PermissionSnapshotRevoked(4, userId),
        ]));

        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.SingleAsync()).Name.Should().Be("Second name");
        (await context.PermissionSnapshots.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyAsync_ReplayOfACommittedPage_IsRecognised_AndWritesNothing()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        ProductId productId = ProductId.New();
        await ApplySuccessfullyAsync(database, new ChangeFeedPage(0, 5, [Product(1, productId, "SKU-1", "Original")]));

        // The same page again, as a pull whose commit the caller never saw would resend it.
        Result<ChangeFeedApplyOutcome> replay = await database.Applier.ApplyAsync(
            new ChangeFeedPage(0, 5, [Product(1, productId, "SKU-1", "Changed on replay")]));

        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().Be(new ChangeFeedApplyOutcome(5, 0, AlreadyApplied: true));
        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.SingleAsync()).Name.Should().Be("Original");
    }

    [Fact]
    public async Task ApplyAsync_TwoAppliersRacingOnePage_ApplyItExactlyOnce()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        ChangeFeedPage page = new(0, 5, [Product(1, ProductId.New(), "SKU-1", "Rice 5kg")]);

        Result<ChangeFeedApplyOutcome>[] results = await Task.WhenAll(
            Task.Run(() => database.Applier.ApplyAsync(page)),
            Task.Run(() => database.Applier.ApplyAsync(page)));

        results.Should().OnlyContain(r => r.IsSuccess);
        results.Select(r => r.Value.AlreadyApplied).Should().BeEquivalentTo([false, true]);
        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.CountAsync()).Should().Be(1);
        (await database.Applier.GetCursorAsync()).Should().Be(5);
    }

    [Fact]
    public async Task ApplyAsync_PageThatDoesNotContinueFromTheCursor_IsRefused()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();

        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(
            new ChangeFeedPage(5, 9, [Product(6, ProductId.New(), "SKU-1", "Skipped ahead")]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sync.feed_cursor_mismatch");
        result.Error.Type.Should().Be(ErrorType.Conflict);
        await AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task ApplyAsync_DatabaseFailureMidPage_RollsBackEarlierChangesAndTheCursor()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();

        // Two products claiming one SKU: the first saves, the second breaks the unique index.
        Func<Task> apply = () => database.Applier.ApplyAsync(new ChangeFeedPage(0, 3,
        [
            Product(1, ProductId.New(), "SKU-DUP", "First"),
            Product(2, ProductId.New(), "SKU-DUP", "Second"),
        ]));

        await apply.Should().ThrowAsync<DbUpdateException>();
        await AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task ReplaceBaselineAsync_ReplacesFeedCachesAndCursorAtomically()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        ProductId oldProductId = ProductId.New();
        ProductId newProductId = ProductId.New();

        await ApplySuccessfullyAsync(database, new ChangeFeedPage(0, 3,
        [Product(1, oldProductId, "OLD", "Old product")]));

        Result<ChangeFeedApplyOutcome> result = await database.Applier.ReplaceBaselineAsync(
            new ChangeFeedBaseline(42,
            [Product(0, newProductId, "NEW", "New product")]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new ChangeFeedApplyOutcome(42, 1, AlreadyApplied: false));
        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.SingleAsync()).Id.Should().Be(newProductId);
        (await context.SyncCursors.SingleAsync()).Position.Should().Be(42);
    }

    public static TheoryData<string, ChangeFeedPage> MalformedPages()
    {
        ProductId productId = ProductId.New();
        UserId userId = UserId.New();
        ChangeFeedChange valid = Product(1, productId, "SKU-1", "Valid first change");

        return new TheoryData<string, ChangeFeedPage>
        {
            { "next cursor before page cursor", new ChangeFeedPage(0, -1, []) },
            { "sequence not after cursor", new ChangeFeedPage(0, 5, [Product(0, productId, "SKU-1", "Rice")]) },
            { "sequences out of order", new ChangeFeedPage(0, 5, [Product(3, productId, "SKU-1", "Rice"), Product(2, productId, "SKU-1", "Rice")]) },
            { "change beyond next cursor", new ChangeFeedPage(0, 1, [valid, Product(2, productId, "SKU-1", "Rice")]) },
            { "blank sku", new ChangeFeedPage(0, 2, [valid, Product(2, productId, " ", "Rice")]) },
            { "empty product id", new ChangeFeedPage(0, 2, [valid, Product(2, default, "SKU-2", "Rice")]) },
            { "price beyond storage scale", new ChangeFeedPage(0, 2, [valid, Price(2, productId, 12.34567m, "PHP")]) },
            { "negative price", new ChangeFeedPage(0, 2, [valid, Price(2, productId, -1m, "PHP")]) },
            { "lowercase currency", new ChangeFeedPage(0, 2, [valid, Price(2, productId, 10m, "php")]) },
            { "price period ends at start", new ChangeFeedPage(0, 2, [valid, new ProductPriceChanged(2, ProductPriceId.New(), productId, null, 10m, "PHP", IssuedAt, IssuedAt)]) },
            { "snapshot expires when issued", new ChangeFeedPage(0, 2, [valid, new PermissionSnapshotIssued(2, userId, 1, IssuedAt, IssuedAt, [])]) },
            { "snapshot repeats a grant", new ChangeFeedPage(0, 2, [valid, Snapshot(2, userId, 1, ("sale.create", null), ("sale.create", null))]) },
            { "unsupported change kind", new ChangeFeedPage(0, 2, [valid, new UnsupportedChange(2)]) },
        };
    }

    [Theory]
    [MemberData(nameof(MalformedPages))]
    public async Task ApplyAsync_MalformedPage_IsRefusedWhole_BeforeAnythingIsWritten(string reason, ChangeFeedPage page)
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();

        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(page);

        result.IsFailure.Should().BeTrue(reason);
        result.Error.Code.Should().Be("sync.feed_page_invalid", reason);
        await AssertNothingWrittenAsync(database);
    }

    private static ProductChanged Product(long sequence, ProductId id, string sku, string name, bool isActive = true)
        => new(sequence, id, sku, name, isActive, TracksBatches: false, TracksExpiry: false, SourceVersion: sequence, IssuedAt);

    private static ProductPriceChanged Price(long sequence, ProductId productId, decimal amount, string currency)
        => new(sequence, ProductPriceId.New(), productId, null, amount, currency, IssuedAt, null);

    private static PermissionSnapshotIssued Snapshot(
        long sequence,
        UserId userId,
        long policyVersion,
        params (string Permission, LocationId? LocationId)[] grants)
        => new(
            sequence,
            userId,
            policyVersion,
            IssuedAt,
            IssuedAt.AddHours(12),
            [.. grants.Select(g => new PermissionSnapshotGrant(g.Permission, g.LocationId))]);

    private static async Task ApplySuccessfullyAsync(TemporaryDeviceDatabase database, ChangeFeedPage page)
    {
        Result<ChangeFeedApplyOutcome> result = await database.Applier.ApplyAsync(page);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
    }

    private static async Task AssertNothingWrittenAsync(TemporaryDeviceDatabase database)
    {
        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.AnyAsync()).Should().BeFalse();
        (await context.ProductPrices.AnyAsync()).Should().BeFalse();
        (await context.PermissionSnapshots.AnyAsync()).Should().BeFalse();
        (await context.SyncCursors.AnyAsync()).Should().BeFalse();
    }

    private sealed record UnsupportedChange(long Sequence) : ChangeFeedChange(Sequence);
}
