using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The owner's read-only monitoring views: each store's sales over a period and
/// each location's stock with its scarcest and most plentiful products.
/// </summary>
[Collection("api")]
public sealed class StoreMonitoringEndpointTests(PosApiFactory factory)
{
    private static long _barcodeSequence = 7700000000000;

    [Fact]
    public async Task StockLevels_ClassifiesEachProduct_AndOrdersScarcestFirst()
    {
        LocationId store = await factory.CreateLocationAsync("SM-ST1", "Monitor Store One");
        ProductId outOfStock = await ProductAsync("SM-OUT");
        ProductId low = await ProductAsync("SM-LOW");
        ProductId healthy = await ProductAsync("SM-OK");
        ProductId over = await ProductAsync("SM-OVER");

        await SettingAsync(store, outOfStock, minimum: 2m, reorder: 5m, target: 10m, maximum: 20m);
        await SettingAsync(store, low, minimum: 2m, reorder: 5m, target: 10m, maximum: 20m);
        await SettingAsync(store, healthy, minimum: 2m, reorder: 5m, target: 10m, maximum: 20m);
        await SettingAsync(store, over, minimum: 2m, reorder: 5m, target: 10m, maximum: 20m);
        await AvailableAsync(store, low, 3m, 10m);
        await AvailableAsync(store, healthy, 12m, 10m);
        await AvailableAsync(store, over, 35m, 10m);

        await factory.CreateUserAsync("sm-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "sm-owner");

        using HttpResponseMessage response = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/inventory/stock-levels?locationId={store.Value:D}"), owner);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement[] rows = [.. document.RootElement.GetProperty("products").EnumerateArray()];
        Dictionary<string, string> statusBySku = rows.ToDictionary(
            r => r.GetProperty("sku").GetString()!, r => r.GetProperty("status").GetString()!);

        statusBySku["SM-OUT"].Should().Be("Out");
        statusBySku["SM-LOW"].Should().Be("Low");
        statusBySku["SM-OK"].Should().Be("Healthy");
        statusBySku["SM-OVER"].Should().Be("Over");

        // Scarcest first: Out, then Low, then Healthy, then Over.
        rows.Select(r => r.GetProperty("sku").GetString()).Should().ContainInOrder("SM-OUT", "SM-LOW", "SM-OK", "SM-OVER");

        JsonElement healthyRow = rows.Single(r => r.GetProperty("sku").GetString() == "SM-OK");
        healthyRow.GetProperty("available").GetDecimal().Should().Be(12m);
        healthyRow.GetProperty("stockValue").GetDecimal().Should().Be(120m);

        // Nothing has sold, so no product has a days-of-cover estimate or a last sale.
        rows.Should().OnlyContain(r => r.GetProperty("daysOfCover").ValueKind == JsonValueKind.Null);
        rows.Should().OnlyContain(r => r.GetProperty("lastSoldOn").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task StockLevels_ForAnotherStore_IsForbiddenToAStoreManager()
    {
        LocationId home = await factory.CreateLocationAsync("SM-HOME", "Monitor Home Store");
        LocationId other = await factory.CreateLocationAsync("SM-OTHER", "Monitor Other Store");
        await factory.CreateUserAsync("sm-manager", Roles.StoreManager, locations: [home], tier: ApprovalTier.Tier1);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "sm-manager");

        using HttpResponseMessage own = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/inventory/stock-levels?locationId={home.Value:D}"), manager);
        own.StatusCode.Should().Be(HttpStatusCode.OK, await own.Content.ReadAsStringAsync());

        using HttpResponseMessage foreign = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/inventory/stock-levels?locationId={other.Value:D}"), manager);
        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InventoryOverview_ForAStore_CountsOnlyThatStore_AndRefusesAStoreNotAssigned()
    {
        LocationId store = await factory.CreateLocationAsync("SM-OV1", "Overview Store");
        LocationId warehouse = await factory.CreateLocationAsync("SM-OV2", "Overview Warehouse");
        ProductId product = await ProductAsync("SM-OV-P");
        await SettingAsync(store, product, minimum: 2m, reorder: 5m, target: 10m, maximum: 20m);
        await SettingAsync(warehouse, product, minimum: 2m, reorder: 5m, target: 10m, maximum: 20m);
        await AvailableAsync(store, product, 3m, 10m);
        await AvailableAsync(warehouse, product, 12m, 10m);

        await factory.CreateUserAsync("sm-ov-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("sm-ov-manager", Roles.StoreManager, locations: [warehouse], tier: ApprovalTier.Tier1);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "sm-ov-owner");
        string manager = await SignInAsync(client, "sm-ov-manager");

        using HttpResponseMessage response = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/dashboard/inventory-overview?locationId={store.Value:D}"), owner);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument overview = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        overview.RootElement.GetProperty("availableQuantity").GetDecimal().Should().Be(3m);
        overview.RootElement.GetProperty("lowStockItems").GetInt32().Should().Be(1);

        using HttpResponseMessage foreign = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/dashboard/inventory-overview?locationId={store.Value:D}"), manager);
        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StorePerformance_ScopesStores_AndFillsEveryDayOfThePeriod()
    {
        LocationId mine = await factory.CreateLocationAsync("SM-PERF1", "Performance Store One");
        LocationId theirs = await factory.CreateLocationAsync("SM-PERF2", "Performance Store Two");
        await factory.CreateUserAsync("sm-perf-manager", Roles.StoreManager, locations: [mine], tier: ApprovalTier.Tier1);
        await factory.CreateUserAsync("sm-perf-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "sm-perf-manager");
        string owner = await SignInAsync(client, "sm-perf-owner");

        const string Period = "from=2026-03-01&to=2026-03-07";

        using HttpResponseMessage managerView = await GetAsync(client, $"/api/v1/dashboard/store-performance?{Period}", manager);
        managerView.StatusCode.Should().Be(HttpStatusCode.OK, await managerView.Content.ReadAsStringAsync());
        using JsonDocument managerReport = JsonDocument.Parse(await managerView.Content.ReadAsStringAsync());
        JsonElement[] managerStores = [.. managerReport.RootElement.GetProperty("stores").EnumerateArray()];
        managerStores.Select(s => s.GetProperty("locationId").GetGuid()).Should().Equal(mine.Value);

        JsonElement store = managerStores[0];
        store.GetProperty("transactions").GetInt32().Should().Be(0);
        store.GetProperty("takings").GetDecimal().Should().Be(0m);
        store.GetProperty("daily").GetArrayLength().Should().Be(7);

        using HttpResponseMessage ownerView = await GetAsync(client, $"/api/v1/dashboard/store-performance?{Period}", owner);
        using JsonDocument ownerReport = JsonDocument.Parse(await ownerView.Content.ReadAsStringAsync());
        ownerReport.RootElement.GetProperty("stores").EnumerateArray()
            .Select(s => s.GetProperty("locationId").GetGuid())
            .Should().Contain([mine.Value, theirs.Value]);
    }

    [Fact]
    public async Task StorePerformance_RejectsAPeriodThatEndsBeforeItStarts()
    {
        await factory.CreateUserAsync("sm-period-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "sm-period-owner");

        using HttpResponseMessage response = await GetAsync(
            client, "/api/v1/dashboard/store-performance?from=2026-03-07&to=2026-03-01", owner);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StoreMovements_ReportsDispatchedUnitsRequestsAndQuietStores()
    {
        LocationId warehouse = await factory.CreateLocationAsync("SM-MOVE-WH", "Movement Warehouse", LocationKind.MainWarehouse);
        LocationId activeStore = await factory.CreateLocationAsync("SM-MOVE1", "Movement Store One", LocationKind.Store);
        LocationId quietStore = await factory.CreateLocationAsync("SM-MOVE2", "Movement Store Two", LocationKind.Store);
        ProductId product = await ProductAsync("SM-MOVE-P");
        await factory.CreateUserAsync("sm-move-requester", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("sm-move-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("sm-move-store", Roles.InventoryStaff, locations: [activeStore]);
        await factory.CreateUserAsync("sm-move-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string requester = await SignInAsync(client, "sm-move-requester");
        string approver = await SignInAsync(client, "sm-move-approver");
        string storeStaff = await SignInAsync(client, "sm-move-store");
        string owner = await SignInAsync(client, "sm-move-owner");

        await AvailableAsync(warehouse, product, quantity: 10m, unitCost: 10m);
        using HttpResponseMessage created = await PostJsonWithTokenAsync(
            client,
            "/api/v1/transfers",
            new
            {
                sourceLocationId = warehouse.Value,
                destinationLocationId = activeStore.Value,
                lines = new object[] { new { productId = product.Value, quantity = 5m } },
            },
            requester);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        using JsonDocument createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid transferId = createdBody.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage submitted = await PostWithTokenAsync(
            client, $"/api/v1/transfers/{transferId:D}/submit", requester);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        using HttpResponseMessage reviewed = await PostJsonWithTokenAsync(
            client, $"/api/v1/transfers/{transferId:D}/review", new { note = "Reviewed for store movement." }, approver);
        reviewed.StatusCode.Should().Be(HttpStatusCode.OK, await reviewed.Content.ReadAsStringAsync());
        using HttpResponseMessage approved = await PostJsonWithTokenAsync(
            client, $"/api/v1/transfers/{transferId:D}/approve", new { note = "Approved for store movement." }, approver);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        using HttpResponseMessage picked = await PostJsonWithTokenAsync(
            client,
            $"/api/v1/transfers/{transferId:D}/pick",
            new { allocations = new object[] { new { lineNo = 1, batchId = (Guid?)null, quantity = 5m } } },
            approver);
        picked.StatusCode.Should().Be(HttpStatusCode.OK, await picked.Content.ReadAsStringAsync());
        using HttpResponseMessage ready = await PostWithTokenAsync(client, $"/api/v1/transfers/{transferId:D}/ready", approver);
        ready.StatusCode.Should().Be(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());
        using HttpResponseMessage dispatched = await PostWithTokenAsync(client, $"/api/v1/transfers/{transferId:D}/dispatch", approver);
        dispatched.StatusCode.Should().Be(HttpStatusCode.OK, await dispatched.Content.ReadAsStringAsync());

        using HttpResponseMessage requestCreated = await PostJsonWithTokenAsync(
            client,
            "/api/v1/transfers/restock-requests",
            new
            {
                storeLocationId = activeStore.Value,
                lines = new object[] { new { productId = product.Value, quantity = 8m } },
            },
            storeStaff);
        requestCreated.StatusCode.Should().Be(HttpStatusCode.OK, await requestCreated.Content.ReadAsStringAsync());
        using JsonDocument requestBody = JsonDocument.Parse(await requestCreated.Content.ReadAsStringAsync());
        Guid requestId = requestBody.RootElement.GetProperty("id").GetGuid();
        using HttpResponseMessage requestSubmitted = await PostWithTokenAsync(
            client, $"/api/v1/transfers/{requestId:D}/submit", storeStaff);
        requestSubmitted.StatusCode.Should().Be(HttpStatusCode.OK, await requestSubmitted.Content.ReadAsStringAsync());

        using HttpResponseMessage response = await GetAsync(client, "/api/v1/dashboard/store-movements", owner);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement report = document.RootElement;
        report.GetProperty("dispatchedTransferCount").GetInt32().Should().Be(1);
        JsonElement[] stores = [.. report.GetProperty("stores").EnumerateArray()];
        stores.Should().HaveCount(2);

        JsonElement active = stores.Single(store => store.GetProperty("locationId").GetGuid() == activeStore.Value);
        active.GetProperty("incomingUnits").GetDecimal().Should().Be(5m);
        active.GetProperty("inboundTransfers").GetInt32().Should().Be(1);
        active.GetProperty("outgoingUnits").GetDecimal().Should().Be(0m);
        active.GetProperty("restockRequests").GetInt32().Should().Be(1);
        active.GetProperty("pendingApprovalRequests").GetInt32().Should().Be(1);
        JsonElement[] recentItems = [.. active.GetProperty("recentItems").EnumerateArray()];
        recentItems.Should().Contain(item =>
            item.GetProperty("productName").GetString() == "Product SM-MOVE-P"
            && item.GetProperty("direction").GetString() == "Received"
            && item.GetProperty("units").GetDecimal() == 5m);
        recentItems.Should().Contain(item =>
            item.GetProperty("productName").GetString() == "Product SM-MOVE-P"
            && item.GetProperty("direction").GetString() == "Requested"
            && item.GetProperty("units").GetDecimal() == 8m);
        active.GetProperty("dailyMovement").EnumerateArray()
            .Sum(point => point.GetProperty("incomingUnits").GetDecimal())
            .Should().Be(5m);

        JsonElement quiet = stores.Single(store => store.GetProperty("locationId").GetGuid() == quietStore.Value);
        quiet.GetProperty("incomingUnits").GetDecimal().Should().Be(0m);
        quiet.GetProperty("outgoingUnits").GetDecimal().Should().Be(0m);
        quiet.GetProperty("restockRequests").GetInt32().Should().Be(0);

        report.GetProperty("recentActivity").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString())
            .Should().Contain(["Store request", "Dispatched transfer"]);
    }

    private Task<ProductId> ProductAsync(string sku)
        => factory.CreateProductAsync(
            sku,
            $"Product {sku}",
            Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture),
            defaultPurchaseCost: 10m);

    private Task<int> SettingAsync(
        LocationId locationId, ProductId productId, decimal minimum, decimal reorder, decimal target, decimal maximum)
        => factory.WithServiceAsync(async context =>
        {
            Result<ProductLocationSetting> created = ProductLocationSetting.Create(
                productId, locationId, isStocked: true, minimum, reorder, target, maximum, target - reorder);
            created.IsSuccess.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
            context.ProductLocationSettings.Add(created.Value);
            return await context.SaveChangesAsync();
        });

    /// <summary>Stocks a bucket directly, as the other inventory tests do.</summary>
    private Task<int> AvailableAsync(LocationId locationId, ProductId productId, decimal quantity, decimal unitCost)
        => factory.WithServiceAsync(context => context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO inventory_balance
                 (location_id, product_id, batch_key, state, quantity,
                  average_unit_cost, total_value, last_movement_id,
                  last_movement_at_utc, version)
             VALUES
                 ({locationId.Value}, {productId.Value}, {Guid.Empty}, {(short)InventoryState.Available},
                  {quantity}, {unitCost}, {quantity * unitCost}, {Guid.CreateVersion7()},
                  {DateTimeOffset.UtcNow}, {0})
             """));

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostWithTokenAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostJsonWithTokenAsync(
        HttpClient client, string path, object body, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None);
    }
}
