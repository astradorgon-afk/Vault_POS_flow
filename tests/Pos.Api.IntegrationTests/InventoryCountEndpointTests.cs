using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using static Pos.Api.IntegrationTests.InventoryControlTestSupport;
using static Pos.Api.IntegrationTests.StockAdjustmentEndpointTests;
using static Pos.Api.IntegrationTests.UserAdministrationEndpointTests;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Inventory counts through the real pipeline: nothing posts before approval, only
/// the variance posts, stock that moved after counting blocks approval, and a
/// product that varies again is flagged and ranked.
/// </summary>
[Collection("api")]
public sealed class InventoryCountEndpointTests(PosApiFactory factory)
{
    private const string Counts = "/api/v1/inventory/counts";

    [Fact]
    public async Task ProductCount_PostsOnlyTheVariance_SoTheBalanceMatchesTheShelf()
    {
        LocationId store = await factory.CreateLocationAsync("CNT-S1", "Count Store 1");
        LocationId writeOff = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);
        ProductId rice = await factory.CreateProductAsync("CNT-01", "Jasmine Rice 5kg", "4800000600015", defaultPurchaseCost: 40m);
        ProductId oil = await factory.CreateProductAsync("CNT-02", "Palm Oil 1L", "4800000600022", defaultPurchaseCost: 90m);
        await factory.CreateUserAsync("cnt1-staff", Roles.InventoryStaff, locations: [store]);
        await factory.CreateUserAsync("cnt1-manager", Roles.StoreManager, locations: [store], tier: ApprovalTier.Tier1);
        using HttpClient client = factory.CreateClient();

        string staff = await SignInAsync(client, "cnt1-staff");
        string manager = await SignInAsync(client, "cnt1-manager");
        await SetBucketAsync(factory, store, rice, 10m, 40m);
        await SetBucketAsync(factory, store, oil, 5m, 90m);

        await ExpectAsync(client, Counts, staff, new { locationId = store.Value, kind = (int)InventoryCountKind.ProductSpecific },
            HttpStatusCode.BadRequest, "count.scope_invalid");

        Guid id = await OpenAsync(client, staff, store, [rice, oil]);

        using (JsonDocument sheet = await GetJsonAsync(client, $"{Counts}/{id}", staff))
        {
            sheet.RootElement.GetProperty("count").GetProperty("number").GetString().Should().StartWith("CNT-");
            sheet.RootElement.GetProperty("lines").EnumerateArray()
                .Select(l => l.GetProperty("systemQuantity").GetDecimal()).Should().BeEquivalentTo([10m, 5m]);
        }

        await RecordAsync(client, staff, id, (rice, 8m));
        await ExpectAsync(client, $"{Counts}/{id}/submit", staff, null, HttpStatusCode.BadRequest, "count.incomplete");
        await RecordAsync(client, staff, id, (oil, 6m));
        await ExpectAsync(client, $"{Counts}/{id}/submit", staff, null, HttpStatusCode.OK);

        (await QuantityAsync(factory, store, rice)).Should().Be(10m, "nothing posts before approval");

        await ExpectAsync(client, $"{Counts}/{id}/approve", staff, null, HttpStatusCode.Forbidden);
        await ExpectAsync(client, $"{Counts}/{id}/approve", manager, null, HttpStatusCode.OK);

        (await QuantityAsync(factory, store, rice)).Should().Be(8m);
        (await QuantityAsync(factory, store, oil)).Should().Be(6m);

        List<InventoryMovement> posted = await MovementsAsync(factory, id);
        posted.Should().HaveCount(4).And.OnlyContain(m => m.ReasonCode == AdjustmentReasonCode.CountCorrection);
        posted.Single(m => m.MovementType == InventoryMovementType.CountAdjustmentDecrease && m.LocationId == writeOff)
            .QuantityDelta.Should().Be(2m);
        posted.Single(m => m.MovementType == InventoryMovementType.CountAdjustmentIncrease && m.LocationId == store)
            .QuantityDelta.Should().Be(1m);

        using JsonDocument variances = await GetJsonAsync(client, $"{Counts}/variances?locationId={store.Value}", manager);
        variances.RootElement.EnumerateArray()
            .Select(v => (v.GetProperty("sku").GetString(), v.GetProperty("variance").GetDecimal(), v.GetProperty("varianceValue").GetDecimal()))
            .Should().BeEquivalentTo([("CNT-01", -2m, -80m), ("CNT-02", 1m, 90m)]);
    }

    [Fact]
    public async Task StockThatMovedAfterCounting_BlocksApproval_UntilTheLineIsCountedAgain()
    {
        LocationId store = await factory.CreateLocationAsync("CNT-S2", "Count Store 2");
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);
        ProductId soap = await factory.CreateProductAsync("CNT-03", "Laundry Soap Bar", "4800000600039", defaultPurchaseCost: 25m);
        await factory.CreateUserAsync("cnt2-staff", Roles.InventoryStaff, locations: [store]);
        await factory.CreateUserAsync("cnt2-manager", Roles.StoreManager, locations: [store], tier: ApprovalTier.Tier1);
        using HttpClient client = factory.CreateClient();

        string staff = await SignInAsync(client, "cnt2-staff");
        string manager = await SignInAsync(client, "cnt2-manager");
        await SetBucketAsync(factory, store, soap, 10m, 25m);

        Guid id = await OpenAsync(client, staff, store, [soap]);
        await RecordAsync(client, staff, id, (soap, 9m));
        await ExpectAsync(client, $"{Counts}/{id}/submit", staff, null, HttpStatusCode.OK);

        // Three bars sold after the shelf was counted.
        await SetBucketAsync(factory, store, soap, 7m, 25m);

        await ExpectAsync(client, $"{Counts}/{id}/approve", manager, null, HttpStatusCode.Conflict, "count.stock_moved_since_counted");
        await ExpectAsync(client, $"{Counts}/{id}/reject", manager, new { reason = "Recount after the sale" }, HttpStatusCode.OK);
        await RecordAsync(client, staff, id, (soap, 6m));
        await ExpectAsync(client, $"{Counts}/{id}/submit", staff, null, HttpStatusCode.OK);
        await ExpectAsync(client, $"{Counts}/{id}/approve", manager, null, HttpStatusCode.OK);

        (await QuantityAsync(factory, store, soap)).Should().Be(6m, "the one missing bar is written off, not the three sold");
    }

    [Fact]
    public async Task AProductThatVariesAgain_IsFlaggedOnSubmission_AndRankedInTheReport()
    {
        LocationId store = await factory.CreateLocationAsync("CNT-S3", "Count Store 3");
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);
        ProductId razors = await factory.CreateProductAsync("CNT-04", "Disposable Razors 5s", "4800000600046", defaultPurchaseCost: 120m);
        await factory.CreateUserAsync("cnt3-staff", Roles.InventoryStaff, locations: [store]);
        await factory.CreateUserAsync("cnt3-manager", Roles.StoreManager, locations: [store], tier: ApprovalTier.Tier1);
        using HttpClient client = factory.CreateClient();

        string staff = await SignInAsync(client, "cnt3-staff");
        string manager = await SignInAsync(client, "cnt3-manager");
        await SetBucketAsync(factory, store, razors, 10m, 120m);

        Guid first = await CountAndApproveAsync(client, staff, manager, store, razors, counted: 9m);
        Guid second = await CountAndApproveAsync(client, staff, manager, store, razors, counted: 7m);

        using (JsonDocument firstDetail = await GetJsonAsync(client, $"{Counts}/{first}", manager))
        {
            firstDetail.RootElement.GetProperty("lines")[0].GetProperty("isRepeatVariance").GetBoolean().Should().BeFalse();
        }

        using (JsonDocument secondDetail = await GetJsonAsync(client, $"{Counts}/{second}", manager))
        {
            secondDetail.RootElement.GetProperty("lines")[0].GetProperty("isRepeatVariance").GetBoolean().Should().BeTrue();
        }

        using (JsonDocument repeats = await GetJsonAsync(client, $"{Counts}/repeat-variances?locationId={store.Value}", manager))
        {
            JsonElement row = repeats.RootElement.EnumerateArray().Should().ContainSingle().Subject;
            row.GetProperty("sku").GetString().Should().Be("CNT-04");
            row.GetProperty("occurrences").GetInt32().Should().Be(2);
            row.GetProperty("netVariance").GetDecimal().Should().Be(-3m);
            row.GetProperty("totalAbsoluteVarianceValue").GetDecimal().Should().Be(360m);
        }

        int flagged = await factory.WithServiceAsync(context => context.AuditLog
            .CountAsync(a => a.EntityId == second && a.Action == AuditActions.Inventory.RepeatVarianceDetected));
        flagged.Should().Be(1);
    }

    [Fact]
    public async Task FullCount_ListsEveryHeldBucket_AndCanBeCancelled()
    {
        LocationId store = await factory.CreateLocationAsync("CNT-S4", "Count Store 4");
        ProductId tea = await factory.CreateProductAsync("CNT-05", "Green Tea 25s", "4800000600053", defaultPurchaseCost: 60m);
        ProductId sugar = await factory.CreateProductAsync("CNT-06", "Brown Sugar 1kg", "4800000600060", defaultPurchaseCost: 70m);
        await factory.CreateUserAsync("cnt4-staff", Roles.InventoryStaff, locations: [store]);
        using HttpClient client = factory.CreateClient();

        string staff = await SignInAsync(client, "cnt4-staff");
        await SetBucketAsync(factory, store, tea, 4m, 60m);
        await SetBucketAsync(factory, store, sugar, 12m, 70m);

        Guid id;
        using (HttpResponseMessage opened = await SendAsync(
            client, HttpMethod.Post, Counts, staff, new { locationId = store.Value, kind = (int)InventoryCountKind.FullPhysical, note = "Whole store" }))
        {
            opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
            id = (await ReadJsonAsync(opened)).RootElement.GetProperty("id").GetGuid();
        }

        using (JsonDocument sheet = await GetJsonAsync(client, $"{Counts}/{id}", staff))
        {
            sheet.RootElement.GetProperty("lines").EnumerateArray()
                .Select(l => l.GetProperty("productId").GetGuid()).Should().BeEquivalentTo([tea.Value, sugar.Value]);
        }

        await ExpectAsync(client, $"{Counts}/{id}/cancel", staff, new { reason = "Delivery arrived mid-count" }, HttpStatusCode.OK);

        using JsonDocument cancelled = await GetJsonAsync(client, $"{Counts}/{id}", staff);
        cancelled.RootElement.GetProperty("count").GetProperty("status").GetString().Should().Be("Cancelled");
    }

    private async Task<Guid> CountAndApproveAsync(
        HttpClient client, string staff, string manager, LocationId store, ProductId product, decimal counted)
    {
        Guid id = await OpenAsync(client, staff, store, [product]);
        await RecordAsync(client, staff, id, (product, counted));
        await ExpectAsync(client, $"{Counts}/{id}/submit", staff, null, HttpStatusCode.OK);
        await ExpectAsync(client, $"{Counts}/{id}/approve", manager, null, HttpStatusCode.OK);
        (await QuantityAsync(factory, store, product)).Should().Be(counted);
        return id;
    }

    private static async Task<Guid> OpenAsync(HttpClient client, string token, LocationId store, ProductId[] products)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Post,
            Counts,
            token,
            new { locationId = store.Value, kind = (int)InventoryCountKind.ProductSpecific, productIds = products.Select(p => p.Value).ToArray() });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).RootElement.GetProperty("id").GetGuid();
    }

    private static Task RecordAsync(HttpClient client, string token, Guid id, params (ProductId Product, decimal Counted)[] lines)
        => ExpectAsync(
            client,
            $"{Counts}/{id}/lines",
            token,
            new { lines = lines.Select(l => new { productId = l.Product.Value, physicalQuantity = l.Counted }).ToArray() },
            HttpStatusCode.OK);
}
