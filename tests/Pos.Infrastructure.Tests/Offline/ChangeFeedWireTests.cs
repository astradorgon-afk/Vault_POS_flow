using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

public sealed class ChangeFeedWireTests
{
    private static readonly DateTimeOffset Now = TemporaryDeviceDatabase.Now;

    [Fact]
    public async Task ReadBaseline_ReadsTheServersBaselineShape_AndTheApplierInstallsIt()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        Guid product = Guid.CreateVersion7();
        Guid store = Guid.CreateVersion7();
        Guid user = Guid.CreateVersion7();

        // Serialized with default options, exactly as SyncBaselineService does:
        // enums arrive as numbers and property names as written.
        (string, JsonElement)[] items =
        [
            Item("ProductChanged", new { productId = product, sku = "RICE-01", name = "Premium Rice 5kg", isActive = true, tracksBatches = false, tracksExpiry = false, sourceVersion = 0L, updatedAtUtc = Now }),
            Item("ProductBarcodeChanged", new { barcode = "4800000000017", productId = product, isPrimary = true, isActive = true }),
            Item("ProductPriceChanged", new { priceId = Guid.CreateVersion7(), productId = product, locationId = (Guid?)null, amount = 245.50m, currency = "PHP", effectiveFromUtc = Now, effectiveToUtc = (DateTimeOffset?)null }),
            Item("LocationChanged", new { locationId = store, code = "STORE01", name = "Store One", kind = LocationKind.Store, timeZoneId = "Asia/Manila", currencyCode = "PHP", isActive = true, settingsJson = (string?)null }),
            Item("UserChanged", new { userId = user, userName = "cashier1", displayName = "Cashier One", isActive = true, securityVersion = 0L }),
            Item("PermissionSnapshotIssued", new { userId = user, policyVersion = 3L, issuedAtUtc = Now, expiresAtUtc = Now.AddHours(72), grants = new[] { new { permission = "shift.open", locationId = (Guid?)store } } }),
        ];

        Result<ChangeFeedBaseline> baseline = ChangeFeedWire.ReadBaseline(17, items);

        baseline.IsSuccess.Should().BeTrue();
        baseline.Value.Changes.Should().HaveCount(6);
        (await database.Applier.ReplaceBaselineAsync(baseline.Value)).IsSuccess.Should().BeTrue();

        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.SingleAsync()).Name.Should().Be("Premium Rice 5kg");
        (await context.ProductPrices.SingleAsync()).Amount.Should().Be(245.50m);
        (await context.Locations.SingleAsync()).Kind.Should().Be(LocationKind.Store);
        (await context.PermissionSnapshots.SingleAsync()).LocationId.Should().Be(new LocationId(store));
        (await context.SyncCursors.SingleAsync()).Position.Should().Be(17);
    }

    [Fact]
    public async Task ReadBaseline_CarriesTheProductCategoryToTheTill_AndToleratesAnOlderServerWithout()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        Guid filed = Guid.CreateVersion7();
        Guid unfiled = Guid.CreateVersion7();

        (string, JsonElement)[] items =
        [
            Item("ProductChanged", new { productId = filed, sku = "RG-1001", name = "Golden Grain Premium Jasmine Rice 5kg", isActive = true, tracksBatches = false, tracksExpiry = false, sourceVersion = 0L, updatedAtUtc = Now, category = "Rice & Grains" }),
            Item("ProductChanged", new { productId = unfiled, sku = "CN-1002", name = "Sea Pearl Tuna Flakes in Oil 155g", isActive = true, tracksBatches = false, tracksExpiry = false, sourceVersion = 0L, updatedAtUtc = Now }),
        ];

        Result<ChangeFeedBaseline> baseline = ChangeFeedWire.ReadBaseline(3, items);
        baseline.IsSuccess.Should().BeTrue();
        (await database.Applier.ReplaceBaselineAsync(baseline.Value)).IsSuccess.Should().BeTrue();

        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.SingleAsync(p => p.Id == new ProductId(filed))).Category.Should().Be("Rice & Grains");
        (await context.Products.SingleAsync(p => p.Id == new ProductId(unfiled))).Category.Should().BeNull();
    }

    [Fact]
    public void Read_RefusesAChangeTypeThisClientDoesNotKnow()
    {
        Result<ChangeFeedChange> result = ChangeFeedWire.Read(4, "SupplierChanged", Element(new { supplierId = Guid.CreateVersion7() }));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sync.feed_page_invalid");
    }

    [Fact]
    public void Read_RefusesAPayloadThatIsNotAnObject()
    {
        Result<ChangeFeedChange> result = ChangeFeedWire.Read(4, "ProductChanged", Element("not an object"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sync.feed_page_invalid");
    }

    [Fact]
    public async Task ReplaceBaselineAsync_RefusesASnapshotCarryingAPermissionADeviceMayNotHold()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        UserId user = UserId.New();

        Result<ChangeFeedApplyOutcome> result = await database.Applier.ReplaceBaselineAsync(new ChangeFeedBaseline(5,
        [
            new PermissionSnapshotIssued(0, user, 3, Now, Now.AddHours(72),
                [new PermissionSnapshotGrant("inventory.adjust.approve", null)]),
        ]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sync.feed_page_invalid");
        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.PermissionSnapshots.AnyAsync()).Should().BeFalse("a refused baseline writes nothing");
    }

    private static (string, JsonElement) Item<T>(string type, T payload) => (type, Element(payload));

    private static JsonElement Element<T>(T payload) => JsonSerializer.SerializeToElement(payload);
}
