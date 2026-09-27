using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Endpoints;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;

namespace Pos.Api.IntegrationTests;

/// <summary>The owner's best- and worst-seller analytics.</summary>
[Collection("api")]
public sealed class ProductAnalyticsTests(PosApiFactory factory)
{
    private static long _barcodeSequence = 7710000000000;

    [Fact]
    public void Best_RanksByRevenue_WithEachShareOfTheStore()
    {
        StoreProductPerformance ranked = ProductRanking.Summarise(Guid.Empty, "S1", "Store", Lines(), byUnits: false, take: 3);

        ranked.Best.Select(p => p.Sku).Should().Equal("RICE", "SOAP", "CANDY");
        ranked.Best[0].Share.Should().Be(62.5m, "500 of the store's 800 net");
        ranked.ProductsSold.Should().Be(3);
        ranked.NetSales.Should().Be(800m);
    }

    [Fact]
    public void Best_ByUnits_PutsTheBusiestFirst()
        => ProductRanking.Summarise(Guid.Empty, "S1", "Store", Lines(), byUnits: true, take: 3)
            .Best.Select(p => p.Sku).Should().Equal("CANDY", "SOAP", "RICE");

    [Fact]
    public void Worst_PutsUnsoldStockFirst_MostStockFirst_AndSkipsDiscontinued()
    {
        StoreProductPerformance ranked = ProductRanking.Summarise(Guid.Empty, "S1", "Store", Lines(), byUnits: false, take: 4);

        ranked.Worst.Select(p => p.Sku).Should().Equal("BLEACH", "GLUE", "CANDY", "SOAP");
        ranked.ProductsNotSold.Should().Be(2, "the discontinued product is not counted against the store");
    }

    [Fact]
    public async Task Endpoint_ListsUnsoldStockAsWorst_AndKeepsAManagerToTheirStore()
    {
        LocationId home = await factory.CreateLocationAsync("PA-HOME", "Analytics Home");
        LocationId other = await factory.CreateLocationAsync("PA-OTHER", "Analytics Other");
        ProductId slow = await ProductAsync("PA-SLOW");
        ProductId heavy = await ProductAsync("PA-HEAVY");
        ProductId elsewhere = await ProductAsync("PA-ELSE");
        await SettingAsync(home, slow);
        await SettingAsync(home, heavy);
        await SettingAsync(other, elsewhere);
        await AvailableAsync(home, slow, 10m);
        await AvailableAsync(home, heavy, 50m);

        await factory.CreateUserAsync("pa-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("pa-manager", Roles.StoreManager, locations: [home], tier: ApprovalTier.Tier1);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "pa-owner");
        string manager = await SignInAsync(client, "pa-manager");

        using JsonDocument ownerView = await GetJsonAsync(client, "/api/v1/dashboard/product-performance?from=2026-03-01&to=2026-03-30", owner);
        JsonElement homeStore = ownerView.RootElement.GetProperty("stores").EnumerateArray()
            .Single(s => s.GetProperty("code").GetString() == "PA-HOME");
        homeStore.GetProperty("best").GetArrayLength().Should().Be(0, "nothing sold in the period");
        homeStore.GetProperty("worst").EnumerateArray().Select(p => p.GetProperty("sku").GetString())
            .Should().StartWith(["PA-HEAVY", "PA-SLOW"], "unsold stock is worst, the most of it first");
        homeStore.GetProperty("productsNotSold").GetInt32().Should().Be(2);
        ownerView.RootElement.GetProperty("business").GetProperty("code").GetString().Should().Be("ALL");

        using JsonDocument managerView = await GetJsonAsync(client, "/api/v1/dashboard/product-performance", manager);
        managerView.RootElement.GetProperty("stores").EnumerateArray().Select(s => s.GetProperty("code").GetString())
            .Should().Equal("PA-HOME");
    }

    [Fact]
    public async Task Endpoint_RejectsAPeriodThatEndsBeforeItStarts()
    {
        await factory.CreateUserAsync("pa-period-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "pa-period-owner");

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/dashboard/product-performance?from=2026-03-07&to=2026-03-01");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static List<ProductSalesLine> Lines() =>
    [
        new(Guid.NewGuid(), "RICE", "Rice 25kg", "Grains", true, Quantity: 5, Net: 500, OnHand: 20),
        new(Guid.NewGuid(), "SOAP", "Soap", "Personal care", true, Quantity: 10, Net: 200, OnHand: 30),
        new(Guid.NewGuid(), "CANDY", "Candy", "Snacks", true, Quantity: 50, Net: 100, OnHand: 5),
        new(Guid.NewGuid(), "GLUE", "Glue", "Office", true, Quantity: 0, Net: 0, OnHand: 4),
        new(Guid.NewGuid(), "BLEACH", "Bleach", "Household", true, Quantity: 0, Net: 0, OnHand: 40),
        new(Guid.NewGuid(), "OLD", "Discontinued", "Household", false, Quantity: 0, Net: 0, OnHand: 99),
    ];

    private Task<ProductId> ProductAsync(string sku)
        => factory.CreateProductAsync(
            sku,
            $"Product {sku}",
            Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture),
            defaultPurchaseCost: 10m);

    private Task<int> SettingAsync(LocationId locationId, ProductId productId)
        => factory.WithServiceAsync(async context =>
        {
            Result<ProductLocationSetting> created = ProductLocationSetting.Create(
                productId, locationId, isStocked: true, 2m, 5m, 10m, 20m, 5m);
            created.IsSuccess.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
            context.ProductLocationSettings.Add(created.Value);
            return await context.SaveChangesAsync();
        });

    private Task<int> AvailableAsync(LocationId locationId, ProductId productId, decimal quantity)
        => factory.WithServiceAsync(context => context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO inventory_balance
                 (location_id, product_id, batch_key, state, quantity,
                  average_unit_cost, total_value, last_movement_id,
                  last_movement_at_utc, version)
             VALUES
                 ({locationId.Value}, {productId.Value}, {Guid.Empty}, {(short)InventoryState.Available},
                  {quantity}, {10m}, {quantity * 10m}, {Guid.CreateVersion7()},
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

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
