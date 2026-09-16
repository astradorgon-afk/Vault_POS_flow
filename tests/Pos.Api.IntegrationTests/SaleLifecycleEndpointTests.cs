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
/// C16: the sale-lifecycle read and accountability surfaces — sales search,
/// receipt reprint, and return detail — through the real pipeline.
/// </summary>
[Collection("api")]
public sealed class SaleLifecycleEndpointTests(PosApiFactory factory)
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 15);

    [Fact]
    public async Task Search_ByStore_ReturnsTheStoresSales_AndHonoursTheFilters()
    {
        Seed seed = await SeedAsync("c16a");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);

        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, quantity: 2m, seq: 1);

        // The store window finds the sale.
        using (JsonDocument found = await SearchAsync(
            client, manager, seed.Store.Value, null, null, null))
        {
            JsonElement root = found.RootElement;
            root.GetArrayLength().Should().Be(1);
            root[0].GetProperty("id").GetGuid().Should().Be(saleId);
            root[0].GetProperty("number").GetString().Should().Be(seed.SaleNumber(1));
            root[0].GetProperty("status").GetString().Should().Be("Completed");
            root[0].GetProperty("netTotal").GetDecimal().Should().Be(90m);
        }

        // The cashier filter narrows to the completing cashier only.
        using (JsonDocument byCashier = await SearchAsync(
            client, manager, seed.Store.Value, null, null, seed.ManagerId.Value))
        {
            byCashier.RootElement.GetArrayLength().Should().Be(1);
        }

        using (JsonDocument byOtherCashier = await SearchAsync(
            client, manager, seed.Store.Value, null, null, Guid.CreateVersion7()))
        {
            byOtherCashier.RootElement.GetArrayLength().Should().Be(0);
        }

        // The business-date window filters the other way.
        using (JsonDocument excluded = await SearchAsync(
            client, manager, seed.Store.Value, BusinessDate.AddDays(1), BusinessDate.AddDays(2), null))
        {
            excluded.RootElement.GetArrayLength().Should().Be(0);
        }

        using (JsonDocument included = await SearchAsync(
            client, manager, seed.Store.Value, BusinessDate.AddDays(-1), BusinessDate.AddDays(1), null))
        {
            included.RootElement.GetArrayLength().Should().Be(1);
        }
    }

    [Fact]
    public async Task Search_ByAStoreManagerOfAnotherStore_IsForbidden()
    {
        Seed seed = await SeedAsync("c16b");
        Seed other = await SeedAsync("c16c");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);
        await CompleteAsync(client, manager, seed, shiftId, quantity: 1m, seq: 1);

        // An auditor may search anywhere — read-only by construction.
        string otherManager = await PinSignInAsync(client, other.ManagerEmployeeCode, other.Device02.Value);
        using HttpResponseMessage outside = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/sales?locationId={seed.Store.Value}"), otherManager);
        outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, await outside.Content.ReadAsStringAsync());

        await factory.CreateUserAsync("c16d-auditor", Roles.Auditor);
        string auditor = await SignInAsync(client, "c16d-auditor");
        using HttpResponseMessage auditing = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/sales?locationId={seed.Store.Value}"), auditor);
        auditing.StatusCode.Should().Be(HttpStatusCode.OK, await auditing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reprint_LogsThePrintAndAudit_AndKeepsTheSaleCompleted()
    {
        Seed seed = await SeedAsync("c16e");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);
        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, quantity: 1m, seq: 1);

        int before = await factory.WithServiceAsync(
            context => context.ReceiptPrints.CountAsync(p => p.SaleId == new SaleId(saleId)));

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            $"/api/v1/sales/{saleId}/reprint",
            new
            {
                locationId = seed.Store.Value,
                deviceId = seed.Device01.Value,
                reason = "The customer asked for another copy",
                reprintedAtUtc = DateTimeOffset.UtcNow,
            },
            manager);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        await factory.WithServiceAsync(async context =>
        {
            var prints = await context.ReceiptPrints
                .Where(p => p.SaleId == new SaleId(saleId))
                .OrderBy(p => p.PrintedAtUtc)
                .ToListAsync();
            prints.Should().HaveCount(before + 1);
            prints[^1].IsReprint.Should().BeTrue();
            prints[^1].Reason.Should().Be("The customer asked for another copy");
            return true;
        });

        // The document itself is untouched by the reprint.
        using JsonDocument detail = await GetSaleDetailAsync(client, manager, saleId);
        detail.RootElement.GetProperty("status").GetString().Should().Be("Completed");
        detail.RootElement.GetProperty("netTotal").GetDecimal().Should().Be(45m);
    }

    [Fact]
    public async Task Reprint_WithoutThePermission_IsForbidden()
    {
        Seed seed = await SeedAsync("c16f");
        using HttpClient client = factory.CreateClient();

        await factory.CreateUserAsync(
            "c16f-cashier", Roles.Cashier, locations: [seed.Store], employeeCode: "c16fCash");
        string cashier = await PinSignInAsync(client, "c16fCash", seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, cashier, seed);
        Guid saleId = await CompleteAsync(client, cashier, seed, shiftId, quantity: 1m, seq: 1);

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            $"/api/v1/sales/{saleId}/reprint",
            new
            {
                locationId = seed.Store.Value,
                deviceId = seed.Device01.Value,
                reason = "Test refusal",
                reprintedAtUtc = DateTimeOffset.UtcNow,
            },
            cashier);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reprint_AtAnotherLocation_IsRefused()
    {
        Seed seed = await SeedAsync("c16g");
        Seed other = await SeedAsync("c16h");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);
        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, quantity: 1m, seq: 1);

        string otherManager = await PinSignInAsync(client, other.ManagerEmployeeCode, other.Device02.Value);

        // Dispatching with the caller's own (wrong) store: the pipeline approves
        // the location because they hold the permission there, and the handler
        // rejects the mismatch — the location must be the sale's own.
        using HttpResponseMessage response = await PostJsonAsync(
            client,
            $"/api/v1/sales/{saleId}/reprint",
            new
            {
                locationId = other.Store.Value,
                deviceId = other.Device02.Value,
                reason = "Test mismatch",
                reprintedAtUtc = DateTimeOffset.UtcNow,
            },
            otherManager);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("sale.receipt.location_mismatch");
    }

    [Fact]
    public async Task ReturnDetail_ShowsLines_AndTheRefundHistory()
    {
        Seed seed = await SeedAsync("c16i", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();

        string manager = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);
        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, quantity: 2m, seq: 1);

        Guid returnId = await CreateReturnAsync(client, manager, seed, shiftId, saleId, 1m, seq: 1);

        using HttpResponseMessage refunded = await PostJsonAsync(
            client,
            $"/api/v1/returns/{returnId}/refund",
            new
            {
                saleId,
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                method = PaymentMethod.Cash,
                amount = 30m,
                tendered = 30m,
                providerReference = (string?)null,
                refundedAtUtc = DateTimeOffset.UtcNow,
            },
            manager);
        refunded.StatusCode.Should().Be(HttpStatusCode.OK, await refunded.Content.ReadAsStringAsync());

        using (JsonDocument detail = await GetReturnDetailAsync(client, manager, returnId))
        {
            JsonElement root = detail.RootElement;
            root.GetProperty("number").GetString().Should().Be(seed.ReturnNumber(1));
            root.GetProperty("isBlind").GetBoolean().Should().BeFalse();
            root.GetProperty("saleId").GetGuid().Should().Be(saleId);
            root.GetProperty("locationId").GetGuid().Should().Be(seed.Store.Value);
            root.GetProperty("refundableTotal").GetDecimal().Should().Be(45m);
            root.GetProperty("refundedTotal").GetDecimal().Should().Be(30m);

            JsonElement line = root.GetProperty("lines")[0];
            line.GetProperty("lineNumber").GetInt32().Should().Be(1);
            line.GetProperty("productId").GetGuid().Should().Be(seed.Product.Value);
            line.GetProperty("productName").GetString().Should().Be("Lifecycle Product c16i");
            line.GetProperty("quantity").GetDecimal().Should().Be(1m);
            line.GetProperty("pendingDispositionQuantity").GetDecimal().Should().Be(1m);
            line.GetProperty("refundableAmount").GetDecimal().Should().Be(45m);

            JsonElement refund = root.GetProperty("refunds")[0];
            refund.GetProperty("method").GetString().Should().Be("Cash");
            refund.GetProperty("amount").GetDecimal().Should().Be(30m);
        }
    }

    [Fact]
    public async Task BlindReturn_Detail_DescribesTheReturn_WithNoSale()
    {
        Seed seed = await SeedAsync("c16j", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();

        string manager = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);
        Guid returnId = await CreateBlindReturnAsync(client, manager, seed, shiftId, 1m, seq: 1, reason: "No receipt");

        using (JsonDocument detail = await GetReturnDetailAsync(client, manager, returnId))
        {
            JsonElement root = detail.RootElement;
            root.GetProperty("isBlind").GetBoolean().Should().BeTrue();
            root.GetProperty("saleId").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("refundableTotal").GetDecimal().Should().Be(45m);
            root.GetProperty("refundedTotal").GetDecimal().Should().Be(0m);
            root.GetProperty("lines")[0].GetProperty("saleItemId").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task ReturnDetail_OfAnUnknownReturn_IsNotFound()
    {
        Seed seed = await SeedAsync("c16k");
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, seed.ManagerUserName);

        using HttpResponseMessage response = await GetAsync(
            client, $"/api/v1/returns/{Guid.CreateVersion7()}", manager);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("sale.return.unknown");
    }

    [Fact]
    public async Task ReturnDetail_ByAStoreManagerOfAnotherStore_IsForbidden()
    {
        Seed seed = await SeedAsync("c16l", shelfQty: 5m);
        Seed other = await SeedAsync("c16m");
        using HttpClient client = factory.CreateClient();

        string manager = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);
        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, quantity: 1m, seq: 1);
        Guid returnId = await CreateReturnAsync(client, manager, seed, shiftId, saleId, 1m, seq: 1);

        string otherManager = await SignInAsync(client, other.ManagerUserName);
        using HttpResponseMessage outside = await GetAsync(client, $"/api/v1/returns/{returnId}", otherManager);
        outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, await outside.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(outside)).Should().Be("sale.return.outside_scope");
    }

    // ---- Seed -------------------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix, decimal shelfQty = 25m)
    {
        LocationId store = await factory.CreateLocationAsync($"SL-{suffix}", $"Lifecycle Store {suffix}", LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalCustomer);
        string managerUserName = $"lif-{suffix}-sm";
        string employeeCode = $"lif{suffix}";
        UserId managerId = await factory.CreateUserAsync(
            managerUserName, Roles.StoreManager, locations: [store], employeeCode: employeeCode);
        string deviceCode = $"LIF{suffix.Substring(suffix.Length - 1)}";
        DeviceId device01 = await factory.CreateDeviceAsync(deviceCode, store);
        DeviceId device02 = await factory.CreateDeviceAsync($"{deviceCode}2", store);

        ProductId product = await factory.CreateProductAsync(
            $"LIF-{suffix}-P1", $"Lifecycle Product {suffix}", $"5100{suffix}0003");
        UnitOfMeasureId unitId = await factory.WithServiceAsync<UnitOfMeasureId>(
            context => context.UnitsOfMeasure
                .AsNoTracking()
                .Where(u => u.Code == "PC")
                .Select(u => u.Id)
                .FirstAsync());

        await factory.CreateUserAsync($"lif-{suffix}-owner", Roles.Owner);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, $"lif-{suffix}-owner");
        using HttpResponseMessage priced = await PostJsonAsync(
            client,
            $"/api/v1/catalog/products/{product.Value}/prices",
            new { amount = 45m, reason = "C16 test price", locationId = store.Value },
            owner);
        priced.StatusCode.Should().Be(HttpStatusCode.Created, await priced.Content.ReadAsStringAsync());

        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, shelfQty, 30m);

        return new Seed(
            store, managerUserName, managerId, employeeCode, deviceCode, device01, device02, product, unitId,
            $"5100{suffix}0003");
    }

    // ---- Request helpers -----------------------------------------------------

    private static async Task<JsonDocument> SearchAsync(
        HttpClient client, string accessToken, Guid locationId, DateOnly? from, DateOnly? to, Guid? cashierId)
    {
        string path = FormattableString.Invariant($"/api/v1/sales?locationId={locationId}");
        if (from is { } fromDate)
        {
            path += FormattableString.Invariant($"&from={fromDate:yyyy-MM-dd}");
        }

        if (to is { } toDate)
        {
            path += FormattableString.Invariant($"&to={toDate:yyyy-MM-dd}");
        }

        if (cashierId is { } cashier)
        {
            path += FormattableString.Invariant($"&cashierId={cashier}");
        }

        using HttpResponseMessage response = await GetAsync(client, path, accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> CompleteAsync(
        HttpClient client, string accessToken, Seed seed, decimal quantity, int seq)
    {
        throw new InvalidOperationException(
            "CompleteAsync requires the shift identifier; use the overload that takes one.");
    }

    private static async Task<Guid> CompleteAsync(
        HttpClient client, string accessToken, Seed seed, Guid shiftId, decimal quantity, int seq)
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
                payments = new[]
                {
                    new { method = (int)PaymentMethod.Cash, amount = 45m * quantity, tendered = 45m * quantity + 5m, providerReference = (string?)null },
                },
            },
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateReturnAsync(
        HttpClient client, string accessToken, Seed seed, Guid shiftId, Guid saleId, decimal quantity, int seq)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/returns",
            new
            {
                number = seed.ReturnNumber(seq),
                eventId = Guid.CreateVersion7(),
                saleId,
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                customerId = (Guid?)null,
                businessDate = BusinessDate,
                returnedAtUtc = DateTimeOffset.UtcNow,
                lines = new[] { new { productId = seed.Product.Value, quantity } },
            },
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadIdAsync(response);
    }

    private static async Task<Guid> CreateBlindReturnAsync(
        HttpClient client, string accessToken, Seed seed, Guid shiftId, decimal quantity, int seq, string reason)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/returns/blind",
            new
            {
                number = seed.ReturnNumber(seq),
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                customerId = (Guid?)null,
                businessDate = BusinessDate,
                returnedAtUtc = DateTimeOffset.UtcNow,
                reason,
                lines = new[] { new { productId = seed.Product.Value, quantity } },
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

    private static async Task<string> SignInManagerAsync(HttpClient client, Seed seed)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/auth/login/pin", UriKind.Relative))
        {
            Content = JsonContent.Create(new { employeeCode = seed.ManagerEmployeeCode, pin = PosApiFactory.TestPin }),
        };
        request.Headers.Add("X-Device-Id", seed.Device01.Value.ToString("D", CultureInfo.InvariantCulture));
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

    private static async Task<JsonDocument> GetReturnDetailAsync(HttpClient client, string accessToken, Guid returnId)
    {
        using HttpResponseMessage response = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/returns/{returnId}"), accessToken);
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

        public string ReturnNumber(long sequence) => DocumentNumber
            .CreateForDevice(DocumentType.SalesReturn, 2026, DeviceCode, sequence).Value;
    }
}