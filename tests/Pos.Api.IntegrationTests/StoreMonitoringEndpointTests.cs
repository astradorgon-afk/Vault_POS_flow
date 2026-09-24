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

        // Nothing has sold, so no product has a days-of-cover estimate.
        rows.Should().OnlyContain(r => r.GetProperty("daysOfCover").ValueKind == JsonValueKind.Null);
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
}
