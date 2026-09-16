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
/// C17: checkout payments through the real pipeline — card, e-wallet and
/// split payment-mix sales, over the documented sale lifecycle (POS.md §3).
/// </summary>
[Collection("api")]
public sealed class SalePaymentEndpointTests(PosApiFactory factory)
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 16);

    [Fact]
    public async Task CardOnly_Sale_Completes_AndTheDetailShowsTheCardPayment()
    {
        Seed seed = await SeedAsync("c17a");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);

        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, quantity: 1m, seq: 1,
            [new PaymentSpec(2 /* Card */, 45m, Tendered: null, "TERMINAL-2001")]);

        using JsonDocument detail = await GetSaleDetailAsync(client, manager, saleId);
        JsonElement root = detail.RootElement;
        root.GetProperty("netTotal").GetDecimal().Should().Be(45m);

        JsonElement payments = root.GetProperty("payments");
        payments.GetArrayLength().Should().Be(1);
        payments[0].GetProperty("method").GetString().Should().Be("Card");
        payments[0].GetProperty("amount").GetDecimal().Should().Be(45m);
        payments[0].TryGetProperty("tendered", out JsonElement tendered).Should().BeTrue();
        tendered.ValueKind.Should().Be(JsonValueKind.Null);
        payments[0].GetProperty("change").ValueKind.Should().Be(JsonValueKind.Null);
        payments[0].GetProperty("providerReference").GetString().Should().Be("TERMINAL-2001");
    }

    [Fact]
    public async Task EWalletOnly_Sale_Completes_AndTheDetailShowsTheEWalletPayment()
    {
        Seed seed = await SeedAsync("c17b");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);

        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, quantity: 2m, seq: 1,
            [new PaymentSpec(3 /* EWallet */, 90m, Tendered: null, "gcash-ref-77")]);

        using JsonDocument detail = await GetSaleDetailAsync(client, manager, saleId);
        JsonElement root = detail.RootElement;
        root.GetProperty("netTotal").GetDecimal().Should().Be(90m);

        JsonElement payments = root.GetProperty("payments");
        payments.GetArrayLength().Should().Be(1);
        payments[0].GetProperty("method").GetString().Should().Be("EWallet");
        payments[0].GetProperty("amount").GetDecimal().Should().Be(90m);
        payments[0].TryGetProperty("tendered", out JsonElement tendered).Should().BeTrue();
        tendered.ValueKind.Should().Be(JsonValueKind.Null);
        payments[0].GetProperty("providerReference").GetString().Should().Be("gcash-ref-77");
    }

    [Fact]
    public async Task SplitCashAndCard_Sale_Completes_AndTheDetailShowsBothPayments()
    {
        Seed seed = await SeedAsync("c17c");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);

        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, quantity: 2m, seq: 1,
        [
            new PaymentSpec(1 /* Cash */, 45m, Tendered: 50m, ProviderReference: null),
            new PaymentSpec(2 /* Card */, 45m, Tendered: null, "TERMINAL-2002"),
        ]);

        using JsonDocument detail = await GetSaleDetailAsync(client, manager, saleId);
        JsonElement root = detail.RootElement;
        root.GetProperty("grossTotal").GetDecimal().Should().Be(90m);
        root.GetProperty("netTotal").GetDecimal().Should().Be(90m);

        JsonElement payments = root.GetProperty("payments");
        payments.GetArrayLength().Should().Be(2);

        JsonElement cash = payments.EnumerateArray().Single(p => p.GetProperty("method").GetString() == "Cash");
        cash.GetProperty("amount").GetDecimal().Should().Be(45m);
        cash.GetProperty("tendered").GetDecimal().Should().Be(50m);
        cash.GetProperty("change").GetDecimal().Should().Be(5m);
        cash.TryGetProperty("providerReference", out JsonElement cashRef).Should().BeTrue();
        cashRef.ValueKind.Should().Be(JsonValueKind.Null);

        JsonElement card = payments.EnumerateArray().Single(p => p.GetProperty("method").GetString() == "Card");
        card.GetProperty("amount").GetDecimal().Should().Be(45m);
        card.GetProperty("change").ValueKind.Should().Be(JsonValueKind.Null);
        card.GetProperty("providerReference").GetString().Should().Be("TERMINAL-2002");
    }

    [Fact]
    public async Task SplitCashAndEWallet_Sale_Completes_AndTheDetailShowsTheRoundTripTotal()
    {
        Seed seed = await SeedAsync("c17d");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);

        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, quantity: 3m, seq: 1,
        [
            new PaymentSpec(1 /* Cash */, 35m, Tendered: 35m, ProviderReference: null),
            new PaymentSpec(3 /* EWallet */, 100m, Tendered: null, "maya-2026-0001"),
        ]);

        using JsonDocument detail = await GetSaleDetailAsync(client, manager, saleId);
        JsonElement root = detail.RootElement;
        root.GetProperty("netTotal").GetDecimal().Should().Be(135m);

        JsonElement payments = root.GetProperty("payments");
        payments.GetArrayLength().Should().Be(2);
        decimal allocated = 0m;
        foreach (JsonElement payment in payments.EnumerateArray())
        {
            allocated += payment.GetProperty("amount").GetDecimal();
        }

        allocated.Should().Be(135m);
    }

    [Fact]
    public async Task SplitPayments_ThatUnderCoverTheTotal_AreRefused()
    {
        Seed seed = await SeedAsync("c17e");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/sales",
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
                    new { method = 1, amount = 20m, tendered = (decimal?)20m, providerReference = (string?)null },
                    new { method = 2, amount = 20m, tendered = (decimal?)null, providerReference = (string?)"TERMINAL-2003" },
                },
            },
            manager);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be(SaleErrors.PaymentMismatch(90m, 40m).Code);
    }

    [Fact]
    public async Task SplitPayments_ThatOverCoverTheTotal_AreRefused()
    {
        Seed seed = await SeedAsync("c17f");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/sales",
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
                    new { method = 1, amount = 50m, tendered = (decimal?)50m, providerReference = (string?)null },
                },
            },
            manager);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be(SaleErrors.PaymentMismatch(45m, 50m).Code);
    }

    // ---- Seeding -------------------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix, decimal shelfQty = 25m)
    {
        LocationId store = await factory.CreateLocationAsync($"SP-{suffix}", $"Payment Store {suffix}", LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalCustomer);
        string managerUserName = $"pay-{suffix}-sm";
        string employeeCode = $"pay{suffix}";
        UserId managerId = await factory.CreateUserAsync(
            managerUserName, Roles.StoreManager, locations: [store], employeeCode: employeeCode);
        string deviceCode = $"SPL{suffix[^1]}";
        DeviceId device01 = await factory.CreateDeviceAsync(deviceCode, store);
        DeviceId device02 = await factory.CreateDeviceAsync($"{deviceCode}2", store);

        ProductId product = await factory.CreateProductAsync(
            $"SP-{suffix}-P1", $"Payment Product {suffix}", $"5200{suffix}0003");
        UnitOfMeasureId unitId = await factory.WithServiceAsync<UnitOfMeasureId>(
            context => context.UnitsOfMeasure
                .AsNoTracking()
                .Where(u => u.Code == "PC")
                .Select(u => u.Id)
                .FirstAsync());

        await factory.CreateUserAsync($"pay-{suffix}-owner", Roles.Owner);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, $"pay-{suffix}-owner");
        using HttpResponseMessage priced = await PostJsonAsync(
            client,
            $"/api/v1/catalog/products/{product.Value}/prices",
            new { amount = 45m, reason = "C17 test price", locationId = store.Value },
            owner);
        priced.StatusCode.Should().Be(HttpStatusCode.Created, await priced.Content.ReadAsStringAsync());

        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, shelfQty, 30m);

        return new Seed(
            store, managerUserName, managerId, employeeCode, deviceCode, device01, device02, product, unitId,
            $"5200{suffix}0003");
    }

    // ---- Request helpers -----------------------------------------------------

    private static async Task<Guid> CompleteAsync(
        HttpClient client, string accessToken, Seed seed, Guid shiftId, decimal quantity, int seq,
        IReadOnlyList<PaymentSpec> payments)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/sales",
            new
            {
                number = seed.SaleNumber(seq),
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
                payments = payments.Select(payment => new
                {
                    method = payment.Method,
                    amount = payment.Amount,
                    tendered = payment.Tendered,
                    providerReference = payment.ProviderReference,
                }).ToArray(),
            },
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadIdAsync(response);
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
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return document.RootElement.GetProperty("accessToken").GetString()!;
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
                businessDate = BusinessDate,
                openingFloat = 0m,
            },
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonDocument> GetSaleDetailAsync(HttpClient client, string accessToken, Guid saleId)
    {
        using HttpResponseMessage response = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/sales/{saleId}"), accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code)
            ? code.GetString()
            : null;
    }

    private sealed record PaymentSpec(int Method, decimal Amount, decimal? Tendered, string? ProviderReference);

    private sealed record Seed(
        LocationId Store,
        string ManagerUserName,
        UserId ManagerId,
        string ManagerEmployeeCode,
        string DeviceCode,
        DeviceId Device01,
        DeviceId Device02,
        ProductId Product,
        UnitOfMeasureId UnitId,
        string Barcode)
    {
        public DeviceId Device => Device01;

        public string SaleNumber(long sequence) => DocumentNumber
            .CreateForDevice(DocumentType.Sale, 2026, DeviceCode, sequence).Value;
    }
}