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
/// C9: the returns HTTP surface through the real pipeline — referenced and
/// blind returns, and referenced/blind refunds, with their cap refusals.
/// </summary>
[Collection("api")]
public sealed class PosReturnsEndpointsTests(PosApiFactory factory)
{
    [Fact]
    public async Task Return_ThenFullCashRefund_AgainstCompletedSale_EndToEnd()
    {
        Seed seed = await SeedAsync("c9a", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();
        string accessToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, accessToken, seed);

        // Complete a cash sale of 2 units (2 x 45 = 90).
        Guid saleId = await CompleteSaleAsync(client, accessToken, seed, shiftId, quantity: 2m, seq: 1);
        decimal beforeQuantity = await InventoryControlTestSupport.QuantityAsync(
            factory, seed.Store, seed.Product);

        // Return 1 unit against the sale.
        using HttpResponseMessage returnResp = await PostJsonAsync(
            client,
            "/api/v1/returns",
            new
            {
                number = DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, seed.DeviceCode, 1).Value,
                eventId = Guid.CreateVersion7(),
                saleId,
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                customerId = (Guid?)null,
                businessDate = new DateOnly(2026, 9, 15),
                returnedAtUtc = DateTimeOffset.UtcNow,
                lines = new[] { new { productId = seed.Product.Value, quantity = 1m } },
            },
            accessToken);
        returnResp.StatusCode.Should().Be(HttpStatusCode.Created, await returnResp.Content.ReadAsStringAsync());
        Guid returnId = await ReadIdAsync(returnResp);
        returnId.Should().NotBeEmpty();

        // The goods moved back into ReturnPending: the sale still deducts shelf,
        // and quantity on the shelf stays as it was after the sale.
        decimal afterReturn = await InventoryControlTestSupport.QuantityAsync(
            factory, seed.Store, seed.Product);
        afterReturn.Should().Be(beforeQuantity, "a return does not restock the sellable shelf");

        // Refund the returned unit's value in cash — capped by the return.
        using HttpResponseMessage ref1 = await PostJsonAsync(
            client,
            $"/api/v1/returns/{returnId}/refund",
            new
            {
                saleId,
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                method = 1,
                amount = 45m,
                tendered = 45m,
                providerReference = (string?)null,
                refundedAtUtc = DateTimeOffset.UtcNow,
            },
            accessToken);
        ref1.StatusCode.Should().Be(HttpStatusCode.OK, await ref1.Content.ReadAsStringAsync());
        (await ReadIdAsync(ref1)).Should().NotBeEmpty();

        // A second refund of the same value pushes the method's running total
        // past what the original cash sale paid (both caps are enforced).
        using HttpResponseMessage ref2 = await PostJsonAsync(
            client,
            $"/api/v1/returns/{returnId}/refund",
            new
            {
                saleId,
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                method = 1,
                amount = 45m,
                tendered = 45m,
                providerReference = (string?)null,
                refundedAtUtc = DateTimeOffset.UtcNow,
            },
            accessToken);
        ref2.StatusCode.Should().Be(HttpStatusCode.Conflict, await ref2.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(ref2)).Should().Be("sale.refund.exceeds_paid_for_method");
    }

    [Fact]
    public async Task Return_QuantityBeyondSale_IsRefused_QuantityExceedsAvailable()
    {
        Seed seed = await SeedAsync("c9b", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();
        string accessToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, accessToken, seed);

        Guid saleId = await CompleteSaleAsync(client, accessToken, seed, shiftId, quantity: 1m, seq: 1);

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/returns",
            new
            {
                number = DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, seed.DeviceCode, 1).Value,
                eventId = Guid.CreateVersion7(),
                saleId,
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                customerId = (Guid?)null,
                businessDate = new DateOnly(2026, 9, 15),
                returnedAtUtc = DateTimeOffset.UtcNow,
                lines = new[] { new { productId = seed.Product.Value, quantity = 2m } },
            },
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("sale.return.quantity_exceeds_available");
    }

    [Fact]
    public async Task BlindReturn_CashRefundAccepted_CardRefundRefused()
    {
        Seed seed = await SeedAsync("c9c", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();
        string accessToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, accessToken, seed);

        // Blind return: no sale, reason required.
        using HttpResponseMessage returnResp = await PostJsonAsync(
            client,
            "/api/v1/returns/blind",
            new
            {
                number = DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, seed.DeviceCode, 1).Value,
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                customerId = (Guid?)null,
                businessDate = new DateOnly(2026, 9, 15),
                returnedAtUtc = DateTimeOffset.UtcNow,
                reason = "Customer lost the receipt",
                lines = new[] { new { productId = seed.Product.Value, quantity = 1m } },
            },
            accessToken);
        returnResp.StatusCode.Should().Be(HttpStatusCode.Created, await returnResp.Content.ReadAsStringAsync());
        Guid returnId = await ReadIdAsync(returnResp);
        returnId.Should().NotBeEmpty();

        // A blind refund is cash only — the command validator rejects the card
        // method up front; no money moves.
        using HttpResponseMessage cardRefund = await PostJsonAsync(
            client,
            $"/api/v1/returns/{returnId}/refund",
            new
            {
                saleId = (Guid?)null,
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                method = 2,
                amount = 45m,
                tendered = (decimal?)null,
                providerReference = "prov-123",
                refundedAtUtc = DateTimeOffset.UtcNow,
            },
            accessToken);
        cardRefund.StatusCode.Should().Be(HttpStatusCode.BadRequest, await cardRefund.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(cardRefund)).Should().Be("sale.refund.blind.cash_only");

        // The cash refund works and draws against the return's cash drawer.
        using HttpResponseMessage cashRefund = await PostJsonAsync(
            client,
            $"/api/v1/returns/{returnId}/refund",
            new
            {
                saleId = (Guid?)null,
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                method = 1,
                amount = 45m,
                tendered = 45m,
                providerReference = (string?)null,
                refundedAtUtc = DateTimeOffset.UtcNow,
            },
            accessToken);
        cashRefund.StatusCode.Should().Be(HttpStatusCode.OK, await cashRefund.Content.ReadAsStringAsync());
        (await ReadIdAsync(cashRefund)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Refund_NamingADifferentSale_IsRefused_SaleMismatch()
    {
        Seed seed = await SeedAsync("c9d", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();
        string accessToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, accessToken, seed);

        Guid saleId = await CompleteSaleAsync(client, accessToken, seed, shiftId, quantity: 1m, seq: 1);

        using HttpResponseMessage returnResp = await PostJsonAsync(
            client,
            "/api/v1/returns",
            new
            {
                number = DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, seed.DeviceCode, 1).Value,
                eventId = Guid.CreateVersion7(),
                saleId,
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                customerId = (Guid?)null,
                businessDate = new DateOnly(2026, 9, 15),
                returnedAtUtc = DateTimeOffset.UtcNow,
                lines = new[] { new { productId = seed.Product.Value, quantity = 1m } },
            },
            accessToken);
        returnResp.StatusCode.Should().Be(HttpStatusCode.Created, await returnResp.Content.ReadAsStringAsync());
        Guid returnId = await ReadIdAsync(returnResp);
        returnId.Should().NotBeEmpty();

        // Refund against a sale the return was not made against.
        using HttpResponseMessage refund = await PostJsonAsync(
            client,
            $"/api/v1/returns/{returnId}/refund",
            new
            {
                saleId = Guid.NewGuid(),
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                method = 1,
                amount = 45m,
                tendered = 45m,
                providerReference = (string?)null,
                refundedAtUtc = DateTimeOffset.UtcNow,
            },
            accessToken);

        refund.StatusCode.Should().Be(HttpStatusCode.Conflict, await refund.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(refund)).Should().Be("sale.refund.sale_mismatch");
    }

    // ---- Seed & helpers -------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix, decimal shelfQty)
    {
        string locationCode = $"C9-{suffix}";
        string locationName = $"C9 Returns Store {suffix}";
        LocationId store = await factory.CreateLocationAsync(locationCode, locationName, LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalCustomer);

        string managerUserName = $"c9pt-{suffix}-sm";
        string employeeCode = $"c9{suffix}";
        await factory.CreateUserAsync(managerUserName, Roles.StoreManager, locations: [store], employeeCode: employeeCode);

        string deviceCode = $"C9{suffix.Substring(suffix.Length - 1)}";
        DeviceId device = await factory.CreateDeviceAsync(deviceCode, store);

        ProductId product = await factory.CreateProductAsync(
            $"C9-{suffix}-P1", $"Returns Product {suffix}", $"5000{suffix}0001");

        UnitOfMeasureId unitId = await factory.WithServiceAsync<UnitOfMeasureId>(
            context => context.UnitsOfMeasure
                .AsNoTracking()
                .Where(u => u.Code == "PC")
                .Select(u => u.Id)
                .FirstAsync());

        await factory.CreateUserAsync($"c9pt-{suffix}-owner", Roles.Owner);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, $"c9pt-{suffix}-owner");
        using HttpResponseMessage priced = await PostJsonAsync(
            client,
            $"/api/v1/catalog/products/{product.Value}/prices",
            new { amount = 45m, reason = "C9 test price", locationId = store.Value },
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

    private static async Task<Guid> CompleteSaleAsync(
        HttpClient client, string accessToken, Seed seed, Guid shiftId, decimal quantity, int seq)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/sales",
            new
            {
                number = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, seq).Value,
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
                payments = new[]
                {
                    new { method = 1, amount = 45m * quantity, tendered = 100m, providerReference = (string?)null },
                },
            },
            accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
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

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
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