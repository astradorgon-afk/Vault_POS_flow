using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Locations;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// C8: Sale-flow error paths and economic effects tested through the real
/// API pipeline — insufficient stock, payment mismatch, tax classification,
/// and void inventory restoration.
/// </summary>
[Collection("api")]
public sealed class PosSalePipelineTests(PosApiFactory factory)
{
    [Fact]
    public async Task Sale_WhenShelfCannotCoverLine_IsRefused_InsufficientStock()
    {
        Seed seed = await SeedAsync("c8a", shelfQty: 3m);
        using HttpClient client = factory.CreateClient();
        string accessToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, accessToken, seed);

        using HttpResponseMessage response = await PostSaleAsync(client, accessToken, seed, quantity: 4m, shiftId);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("inventory.insufficient_stock");
    }

    [Fact]
    public async Task Sale_WhenPaymentsDoNotCoverTotal_IsRefused_PaymentMismatch()
    {
        Seed seed = await SeedAsync("c8b", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string accessToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, accessToken, seed);

        // Product at 45, qty 2 = 90. Tender only 80.
        using HttpResponseMessage response = await PostSaleAsync(
            client, accessToken, seed, quantity: 2m, shiftId, new[]
            {
                new { method = 1, amount = 80m, tendered = 80m, providerReference = (string?)null },
            });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("sale.payment_mismatch");
    }

    [Fact]
    public async Task Sale_Line_VatClassification_IsRecorded()
    {
        Seed seed = await SeedAsync("c8c", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string accessToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, accessToken, seed);

        using HttpResponseMessage createResp = await PostSaleAsync(client, accessToken, seed, quantity: 1m, shiftId: shiftId);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created, await createResp.Content.ReadAsStringAsync());

        using JsonDocument createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        Guid saleId = createDoc.RootElement.GetProperty("id").GetGuid();

        // GET the sale detail — the POST body only contains { id }.
        using HttpResponseMessage detailResp = await GetJsonAsync(client, $"/api/v1/sales/{saleId}", accessToken);
        detailResp.StatusCode.Should().Be(HttpStatusCode.OK, await detailResp.Content.ReadAsStringAsync());

        using JsonDocument detailDoc = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync());
        JsonElement line = detailDoc.RootElement.GetProperty("lines")[0];

        // The default product is not VAT-exempt; it is taxed at the store's
        // location VAT rate (12%). Zero-rating has no product flag yet (§2.6).
        line.GetProperty("vatRate").GetDecimal().Should().Be(0.12m);
        line.GetProperty("isVatExempt").GetBoolean().Should().BeFalse();
        line.GetProperty("isZeroRated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task VoidingSale_RestoresShelfQuantity()
    {
        Seed seed = await SeedAsync("c8d", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();
        string accessToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, accessToken, seed);

        decimal before = await InventoryControlTestSupport.QuantityAsync(factory, seed.Store, seed.Product);

        // Complete a sale of 2 units.
        using HttpResponseMessage saleResp = await PostSaleAsync(client, accessToken, seed, quantity: 2m, shiftId: shiftId);
        saleResp.StatusCode.Should().Be(HttpStatusCode.Created, await saleResp.Content.ReadAsStringAsync());

        using JsonDocument saleDoc = JsonDocument.Parse(await saleResp.Content.ReadAsStringAsync());
        Guid saleId = saleDoc.RootElement.GetProperty("id").GetGuid();

        decimal afterSale = await InventoryControlTestSupport.QuantityAsync(factory, seed.Store, seed.Product);
        afterSale.Should().Be(before - 2m, "sale should deduct from shelf");

        // Void the sale — shelf should return to the original quantity.
        using HttpResponseMessage voidResp = await PostJsonAsync(
            client,
            $"/api/v1/sales/{saleId}/void",
            new
            {
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                businessDate = new DateOnly(2026, 9, 15),
                voidedAtUtc = DateTimeOffset.UtcNow,
                reason = "Customer changed mind",
            },
            accessToken);
        voidResp.StatusCode.Should().Be(HttpStatusCode.OK, await voidResp.Content.ReadAsStringAsync());

        decimal afterVoid = await InventoryControlTestSupport.QuantityAsync(factory, seed.Store, seed.Product);
        afterVoid.Should().Be(before, "void must restore the shelf to its pre-sale quantity");
    }

    // ---- Seed & helpers -------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix, decimal shelfQty)
    {
        string locationCode = $"C8-{suffix}";
        string locationName = $"C8 Pipeline Store {suffix}";
        LocationId store = await factory.CreateLocationAsync(locationCode, locationName, LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalCustomer);

        string managerUserName = $"c8pt-{suffix}-sm";
        string employeeCode = $"c8{suffix}";
        await factory.CreateUserAsync(managerUserName, Roles.StoreManager, locations: [store], employeeCode: employeeCode);

        string deviceCode = $"C8{suffix.Substring(suffix.Length - 1)}";
        DeviceId device = await factory.CreateDeviceAsync(deviceCode, store);

        ProductId product = await factory.CreateProductAsync(
            $"C8-{suffix}-P1", $"Pipeline Product {suffix}", $"5000{suffix}0001");

        UnitOfMeasureId unitId = await factory.WithServiceAsync<UnitOfMeasureId>(
            context => context.UnitsOfMeasure
                .AsNoTracking()
                .Where(u => u.Code == "PC")
                .Select(u => u.Id)
                .FirstAsync());

        await factory.CreateUserAsync($"c8pt-{suffix}-owner", Roles.Owner);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, $"c8pt-{suffix}-owner");
        using HttpResponseMessage priced = await PostJsonAsync(
            client,
            $"/api/v1/catalog/products/{product.Value}/prices",
            new { amount = 45m, reason = "C8 test price", locationId = store.Value },
            owner);
        priced.StatusCode.Should().Be(HttpStatusCode.Created, await priced.Content.ReadAsStringAsync());

        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, shelfQty, 30m);

        return new Seed(store, managerUserName, employeeCode, deviceCode, device, product, unitId, $"5000{suffix}0001");
    }

    private static async Task<string> SignInManagerAsync(HttpClient client, Seed seed)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/auth/login/pin", UriKind.Relative))
        {
            Content = JsonContent.Create(new { employeeCode = seed.EmployeeCode, pin = PosApiFactory.TestPin }),
        };
        request.Headers.Add("X-Device-Id", seed.Device.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<Guid> OpenShiftAsync(HttpClient client, string accessToken, Seed seed)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/shifts/open",
            new
            {
                number = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value,
                locationId = seed.Store.Value,
                businessDate = new DateOnly(2026, 9, 15),
                openingFloat = 100m,
            },
            accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> PostSaleAsync(
        HttpClient client,
        string accessToken,
        Seed seed,
        decimal quantity,
        Guid shiftId,
        object[]? payments = null)
    {
        payments ??= new[]
        {
            new { method = 1, amount = 45m * quantity, tendered = 100m, providerReference = (string?)null },
        };

        return await PostJsonAsync(
            client,
            "/api/v1/sales",
            new
            {
                number = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value,
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                cashierShiftId = shiftId,
                deviceId = seed.Device.Value,
                customerId = (Guid?)null,
                businessDate = new DateOnly(2026, 9, 15),
                completedAtUtc = DateTimeOffset.UtcNow,
                lines = new[]
                {
                    new
                    {
                        productId = seed.Product.Value,
                        quantity,
                        unitOfMeasureId = seed.UnitId.Value,
                        barcode = seed.Barcode,
                        unitPriceOverride = (decimal?)null,
                        priceOverrideAuthorizedByUserId = (Guid?)null,
                        discount = 0m,
                        discountAuthorizedByUserId = (Guid?)null,
                        allowExpiredOverride = false,
                    },
                },
                payments,
            },
            accessToken);
    }

    private static async Task<HttpResponseMessage> GetJsonAsync(
        HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client, string path, object body, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }

    private sealed record Seed(
        LocationId Store,
        string ManagerUserName,
        string EmployeeCode,
        string DeviceCode,
        DeviceId Device,
        ProductId Product,
        UnitOfMeasureId UnitId,
        string Barcode);
}
