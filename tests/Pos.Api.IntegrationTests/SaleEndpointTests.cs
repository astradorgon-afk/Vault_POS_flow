using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Sales;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The SAL-numbered sale and its receipt (C6): completed atomically through the
/// real pipeline against a device-resident shift, read and printed by the roles
/// entitled to the branch, and refused to everyone else.
/// </summary>
[Collection("api")]
public sealed class SaleEndpointTests(PosApiFactory factory)
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 15);

    [Fact]
    public async Task Complete_ThenRead_AndPrint_ReflectsTheSale()
    {
        Seed seed = await SeedAsync("se1");
        using HttpClient client = factory.CreateClient();

        string cashier = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, cashier, seed);

        Guid saleId = await CompleteAsync(
            client,
            cashier,
            new
            {
                number = seed.SaleNumber(1),
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                cashierShiftId = shiftId,
                deviceId = seed.Device01.Value,
                customerId = (Guid?)null,
                businessDate = BusinessDate,
                completedAtUtc = DateTimeOffset.UtcNow,
                lines = new[]
                {
                    new
                    {
                        productId = seed.Product.Value,
                        quantity = 2m,
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
                    new
                    {
                        method = PaymentMethod.Cash,
                        amount = 90m,
                        tendered = 200m,
                        providerReference = (string?)null,
                    },
                },
            });

        using (JsonDocument detail = await GetSaleDetailAsync(client, cashier, saleId))
        {
            JsonElement root = detail.RootElement;
            root.GetProperty("id").GetGuid().Should().Be(saleId);
            root.GetProperty("number").GetString().Should().Be(seed.SaleNumber(1));
            root.GetProperty("status").GetString().Should().Be("Completed");
            root.GetProperty("locationId").GetGuid().Should().Be(seed.Store.Value);
            root.GetProperty("cashierShiftId").GetGuid().Should().Be(shiftId);
            root.GetProperty("deviceId").GetGuid().Should().Be(seed.Device01.Value);
            root.GetProperty("completedByUserId").GetGuid().Should().Be(seed.ManagerId.Value);
            root.GetProperty("businessDate").GetString().Should().Be(BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            root.GetProperty("grossTotal").GetDecimal().Should().Be(90m);
            root.GetProperty("netTotal").GetDecimal().Should().Be(90m);

            JsonElement line = root.GetProperty("lines")[0];
            line.GetProperty("lineNumber").GetInt32().Should().Be(1);
            line.GetProperty("productId").GetGuid().Should().Be(seed.Product.Value);
            line.GetProperty("productName").GetString().Should().Be("Sale Test Product");
            line.GetProperty("quantity").GetDecimal().Should().Be(2m);
            line.GetProperty("unitPrice").GetDecimal().Should().Be(45m);
            line.GetProperty("grossAmount").GetDecimal().Should().Be(90m);
            line.GetProperty("netAmount").GetDecimal().Should().Be(90m);

            JsonElement payment = root.GetProperty("payments")[0];
            payment.GetProperty("method").GetString().Should().Be("Cash");
            payment.GetProperty("amount").GetDecimal().Should().Be(90m);
            payment.GetProperty("tendered").GetDecimal().Should().Be(200m);
            payment.GetProperty("change").GetDecimal().Should().Be(110m);
        }

        using (HttpResponseMessage printed = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/sales/{saleId}/receipt"), cashier))
        {
            printed.StatusCode.Should().Be(HttpStatusCode.OK, await printed.Content.ReadAsStringAsync());
            printed.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");

            string text = await printed.Content.ReadAsStringAsync();
            text.Should().StartWith("SALE RECEIPT");
            text.Should().Contain(seed.SaleNumber(1));
            text.Should().Contain("Location: Sale Store se1");
            text.Should().Contain("(Asia/Manila)");
            text.Should().Contain("Sale Test Product");
            text.Should().Contain("TOTAL: 90.00");
            text.Should().Contain("Paid by Cash: 90.00");
            text.Should().Contain("Tendered: 200.00");
            text.Should().Contain("Change: 110.00");
            text.Should().Contain("Thank you.");
        }

        // Getting the receipt is the first print, and must be logged.
        int prints = await factory.WithServiceAsync(
            context => context.ReceiptPrints.CountAsync(p => p.SaleId == new SaleId(saleId)));
        prints.Should().Be(1);
    }

    [Fact]
    public async Task Complete_WhenTheProductHasNoPrice_IsRefused()
    {
        Seed seed = await SeedAsync("se2", withPrice: false);
        using HttpClient client = factory.CreateClient();

        string cashier = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, cashier, seed);

        using HttpResponseMessage response = await PostSaleAsync(
            client,
            cashier,
            new
            {
                number = seed.SaleNumber(1),
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                cashierShiftId = shiftId,
                deviceId = seed.Device01.Value,
                customerId = (Guid?)null,
                businessDate = BusinessDate,
                completedAtUtc = DateTimeOffset.UtcNow,
                lines = new[]
                {
                    new
                    {
                        productId = seed.Product.Value,
                        quantity = 1m,
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
                    new
                    {
                        method = PaymentMethod.Cash,
                        amount = 10m,
                        tendered = 10m,
                        providerReference = (string?)null,
                    },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("sale.item.price_missing");
    }

    [Fact]
    public async Task Complete_ExceedingTheSellableShelf_IsRefused()
    {
        Seed seed = await SeedAsync("se3");
        await InventoryControlTestSupport.SetBucketAsync(factory, seed.Store, seed.Product, 5m, 30m);
        using HttpClient client = factory.CreateClient();

        string cashier = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, cashier, seed);

        using HttpResponseMessage response = await PostSaleAsync(
            client,
            cashier,
            new
            {
                number = seed.SaleNumber(1),
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                cashierShiftId = shiftId,
                deviceId = seed.Device01.Value,
                customerId = (Guid?)null,
                businessDate = BusinessDate,
                completedAtUtc = DateTimeOffset.UtcNow,
                lines = new[]
                {
                    new
                    {
                        productId = seed.Product.Value,
                        quantity = 6m,
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
                    new
                    {
                        method = PaymentMethod.Cash,
                        amount = 270m,
                        tendered = 270m,
                        providerReference = (string?)null,
                    },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("inventory.insufficient_stock");
    }

    [Fact]
    public async Task Read_ByAStoreManagerOfAnotherStore_IsForbidden_ButAuditorSeesIt()
    {
        Seed seed = await SeedAsync("se4");
        Seed other = await SeedAsync("se4b");
        await factory.CreateUserAsync("se4-auditor", Roles.Auditor);
        using HttpClient client = factory.CreateClient();

        string cashier = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, cashier, seed);
        Guid saleId = await CompleteAsync(
            client,
            cashier,
            new
            {
                number = seed.SaleNumber(1),
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                cashierShiftId = shiftId,
                deviceId = seed.Device01.Value,
                customerId = (Guid?)null,
                businessDate = BusinessDate,
                completedAtUtc = DateTimeOffset.UtcNow,
                lines = new[]
                {
                    new
                    {
                        productId = seed.Product.Value,
                        quantity = 1m,
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
                    new
                    {
                        method = PaymentMethod.Cash,
                        amount = 45m,
                        tendered = 50m,
                        providerReference = (string?)null,
                    },
                },
            });

        string otherManager = await PinSignInAsync(client, other.ManagerEmployeeCode, other.Device01.Value);

        foreach (string path in new[] { string.Empty, "/receipt" })
        {
            using HttpResponseMessage outside = await GetAsync(
                client, FormattableString.Invariant($"/api/v1/sales/{saleId}{path}"), otherManager);
            outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, await outside.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(outside)).Should().Be("sale.outside_scope");
        }

        string auditor = await SignInAsync(client, "se4-auditor");
        using (JsonDocument detail = await GetSaleDetailAsync(client, auditor, saleId))
        {
            detail.RootElement.GetProperty("number").GetString().Should().Be(seed.SaleNumber(1));
        }
    }

    [Fact]
    public async Task Detail_OfAnUnknownSale_ReturnsNotFound()
    {
        Seed seed = await SeedAsync("se5");
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, seed.ManagerUserName);

        using HttpResponseMessage response = await GetAsync(
            client, $"/api/v1/sales/{Guid.CreateVersion7()}", manager);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("sale.unknown");
    }

    // ---- Seed -------------------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix, bool withPrice = true)
    {
        LocationId store = await factory.CreateLocationAsync($"SL-{suffix}", $"Sale Store {suffix}", LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalCustomer);
        string managerUserName = $"slt-{suffix}-sm";
        string employeeCode = $"sl{suffix}";
        UserId managerId = await factory.CreateUserAsync(
            managerUserName, Roles.StoreManager, locations: [store], employeeCode: employeeCode);
        string deviceCode = $"SL{suffix.Substring(suffix.Length - 1)}";
        DeviceId device = await factory.CreateDeviceAsync(deviceCode, store);

        ProductId product = await factory.CreateProductAsync(
            $"SL-{suffix}-P1", "Sale Test Product", $"4800{suffix}0002");
        UnitOfMeasureId unitId = await factory.WithServiceAsync<UnitOfMeasureId>(
            context => context.UnitsOfMeasure
                .AsNoTracking()
                .Where(u => u.Code == "PC")
                .Select(u => u.Id)
                .FirstAsync());

        // The selling price is scheduled through the real endpoint, exactly as a
        // price manager would: only price managers may change it, and the sale
        // handler re-derives it from the PriceAt snapshot. Left out entirely when
        // a test wants the price-missing refusal.
        if (withPrice)
        {
            await factory.CreateUserAsync($"slt-{suffix}-owner", Roles.Owner);
            using HttpClient client = factory.CreateClient();
            string owner = await SignInAsync(client, $"slt-{suffix}-owner");
            using HttpResponseMessage priced = await PostJsonAsync(
                client,
                FormattableString.Invariant($"/api/v1/catalog/products/{product.Value}/prices"),
                new { amount = 45m, reason = "Test launch price", locationId = store.Value },
                owner);
            priced.StatusCode.Should().Be(HttpStatusCode.Created, await priced.Content.ReadAsStringAsync());
        }

        // No production flow yet supplies retail stock, so the sellable shelf is
        // seeded directly, the same way the inventory control tests do.
        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, 25m, 30m);

        return new Seed(
            store, managerUserName, managerId, employeeCode, deviceCode, device, product, unitId, "4800" + suffix + "0002");
    }

    // ---- Request helpers ------------------------------------------------------

    private static string SaleNumber(string deviceCode, long sequence)
        => DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, deviceCode, sequence).Value;

    private static string ShiftNumber(string deviceCode, long sequence)
        => DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, deviceCode, sequence).Value;

    private static async Task<Guid> OpenShiftAsync(HttpClient client, string accessToken, Seed seed)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/shifts/open",
            new
            {
                number = ShiftNumber(seed.DeviceCode, 1),
                locationId = seed.Store.Value,
                businessDate = BusinessDate,
                openingFloat = 0m,
            },
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CompleteAsync(HttpClient client, string accessToken, object body)
    {
        using HttpResponseMessage response = await PostSaleAsync(client, accessToken, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> PostSaleAsync(HttpClient client, string accessToken, object body)
        => await PostJsonAsync(client, "/api/v1/sales", body, accessToken);

    private static async Task<JsonDocument> GetSaleDetailAsync(HttpClient client, string accessToken, Guid saleId)
    {
        using HttpResponseMessage response = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/sales/{saleId}"), accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> PinSignInAsync(HttpClient client, string employeeCode, Guid deviceId)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/auth/login/pin", UriKind.Relative))
        {
            Content = JsonContent.Create(new { employeeCode, pin = PosApiFactory.TestPin }),
        };

        request.Headers.Add("X-Device-Id", deviceId.ToString("D", CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
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
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("errorCode", out JsonElement code)
            ? code.GetString()
            : null;
    }

    private sealed record Seed(
        LocationId Store,
        string ManagerUserName,
        UserId ManagerId,
        string ManagerEmployeeCode,
        string DeviceCode,
        DeviceId Device01,
        ProductId Product,
        UnitOfMeasureId UnitId,
        string Barcode)
    {
        public string SaleNumber(long sequence) => SaleEndpointTests.SaleNumber(DeviceCode, sequence);
    }
}