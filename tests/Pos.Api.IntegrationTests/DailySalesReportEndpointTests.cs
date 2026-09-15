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
/// The daily-sales summary (ROADMAP Phase 11 / C7): one endpoint that returns
/// aggregated totals, per-payment-method breakdowns, per-shift rows, and refund
/// amounts for a location on a business date.
/// </summary>
[Collection("api")]
public sealed class DailySalesReportEndpointTests(PosApiFactory factory)
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 15);

    [Fact]
    public async Task Aggregates_Sales_Payments_And_Shifts()
    {
        Seed seed = await SeedAsync("dr1");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);

        // Sale 1: 2 × 45 = 90, paid entirely in cash (tendered 200, change 110).
        Guid sale1Id = await CompleteAsync(client, manager, seed, shiftId, seed.Product, 2m, seed.SaleNumber(1), new[] { (PaymentMethod.Cash, 90m, (decimal?)200m, (string?)null) });

        // Sale 2: 1 × 45 = 45, paid split: 20 cash + 25 card.
        Guid sale2Id = await CompleteAsync(client, manager, seed, shiftId, seed.Product, 1m, seed.SaleNumber(2), new[]
        {
            (PaymentMethod.Cash, 20m, (decimal?)20m, (string?)null),
            (PaymentMethod.Card, 25m, (decimal?)null, (string?)$"card-tkn-{Guid.NewGuid():N}"),
        });

        using JsonDocument report = await GetReportAsync(client, manager, seed.Store, BusinessDate);
        JsonElement root = report.RootElement;

        root.GetProperty("locationId").GetGuid().Should().Be(seed.Store.Value);
        root.GetProperty("locationName").GetString().Should().Be(seed.LocationName);
        root.GetProperty("businessDate").GetString().Should().Be("2026-09-15");

        // ---- Sales summary
        JsonElement sales = root.GetProperty("salesSummary");
        sales.GetProperty("salesCount").GetInt32().Should().Be(2);
        sales.GetProperty("grossTotal").GetDecimal().Should().Be(135m);
        sales.GetProperty("discountTotal").GetDecimal().Should().Be(0m);
        sales.GetProperty("netTotal").GetDecimal().Should().Be(135m);
        sales.GetProperty("refundTotal").GetDecimal().Should().Be(0m);

        // ---- Payments by method
        JsonElement payments = root.GetProperty("paymentsByMethod");
        payments.GetArrayLength().Should().Be(2);

        Dictionary<string, (decimal Amount, decimal ChangeGiven)> byMethod = payments
            .EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("method").GetString()!,
                e => (e.GetProperty("amount").GetDecimal(), e.GetProperty("changeGiven").GetDecimal()));

        byMethod["Cash"].Should().Be((110m, 110m)); // 90 + 20, change 110 + 0
        byMethod["Card"].Should().Be((25m, 0m));

        // ---- Shift row
        JsonElement shifts = root.GetProperty("shifts");
        shifts.GetArrayLength().Should().Be(1);

        JsonElement shift = shifts[0];
        shift.GetProperty("shiftId").GetGuid().Should().Be(shiftId);
        shift.GetProperty("status").GetString().Should().Be("Open");
        shift.GetProperty("salesCount").GetInt32().Should().Be(2);
        shift.GetProperty("netTotal").GetDecimal().Should().Be(135m);
        shift.GetProperty("cashSalesTotal").GetDecimal().Should().Be(110m);
        shift.GetProperty("cashRefundsTotal").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task IncludesRefundsIssuedAgainstReturns()
    {
        Seed seed = await SeedAsync("dr2");
        using HttpClient client = factory.CreateClient();

        string manager = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device.Value);
        Guid shiftId = await OpenShiftAsync(client, manager, seed);

        // Complete a sale: 2 × 45 = 90 paid in cash (tendered 100, change 10).
        Guid saleId = await CompleteAsync(client, manager, seed, shiftId, seed.Product, 2m, seed.SaleNumber(1), new[] { (PaymentMethod.Cash, 90m, (decimal?)100m, (string?)null) });

        // Return 1 of the 2 units and issue a 45 cash refund against the same shift.
        await SeedReturnAndRefundAsync(
            factory,
            new SaleId(saleId),
            new CashierShiftId(shiftId),
            seed.Device);

        using JsonDocument report = await GetReportAsync(client, manager, seed.Store, BusinessDate);
        JsonElement root = report.RootElement;

        root.GetProperty("salesSummary").GetProperty("salesCount").GetInt32().Should().Be(1);
        root.GetProperty("salesSummary").GetProperty("grossTotal").GetDecimal().Should().Be(90m);
        root.GetProperty("salesSummary").GetProperty("netTotal").GetDecimal().Should().Be(90m);
        root.GetProperty("salesSummary").GetProperty("refundTotal").GetDecimal().Should().Be(45m);

        JsonElement shift = root.GetProperty("shifts")[0];
        shift.GetProperty("cashSalesTotal").GetDecimal().Should().Be(90m);
        shift.GetProperty("cashRefundsTotal").GetDecimal().Should().Be(45m);
    }

    [Fact]
    public async Task Report_ForAStoreManagerOfAnotherStore_IsRefused_ButAuditorSeesIt()
    {
        Seed seed = await SeedAsync("dr3");
        Seed other = await SeedAsync("dr3b");
        await factory.CreateUserAsync("dr3-auditor", Roles.Auditor);
        using HttpClient client = factory.CreateClient();

        string otherManager = await PinSignInAsync(client, other.ManagerEmployeeCode, other.Device.Value);

        using HttpResponseMessage outside = await GetReportRawAsync(client, otherManager, seed.Store, BusinessDate);
        outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, await outside.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(outside)).Should().Be("report.outside_scope");

        // Auditor has AllLocations and may read any store's report.
        string auditor = await SignInAsync(client, "dr3-auditor");
        using JsonDocument report = await GetReportAsync(client, auditor, seed.Store, BusinessDate);
        report.RootElement.GetProperty("locationId").GetGuid().Should().Be(seed.Store.Value);
    }

    [Fact]
    public async Task Report_ForAnUnknownLocation_ReturnsNotFound()
    {
        Seed seed = await SeedAsync("dr4");
        await factory.CreateUserAsync("dr4-auditor", Roles.Auditor);
        using HttpClient client = factory.CreateClient();
        string auditor = await SignInAsync(client, "dr4-auditor");

        using HttpResponseMessage response = await GetReportRawAsync(
            client, auditor, LocationId.New(), BusinessDate);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("report.location_unknown");
    }

    // ---- Seed -----------------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix)
    {
        string locationCode = $"DR-{suffix}";
        string locationName = $"Sales Report Store {suffix}";
        LocationId store = await factory.CreateLocationAsync(locationCode, locationName, LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalCustomer);

        string managerUserName = $"drpt-{suffix}-sm";
        string employeeCode = $"dr{suffix}";
        UserId managerId = await factory.CreateUserAsync(
            managerUserName, Roles.StoreManager, locations: [store], employeeCode: employeeCode);

        string deviceCode = $"DR{suffix.Substring(suffix.Length - 1)}";
        DeviceId device = await factory.CreateDeviceAsync(deviceCode, store);

        ProductId product = await factory.CreateProductAsync(
            $"DR-{suffix}-P1", $"Report Product {suffix}", $"4900{suffix}0001");

        UnitOfMeasureId unitId = await factory.WithServiceAsync<UnitOfMeasureId>(
            context => context.UnitsOfMeasure
                .AsNoTracking()
                .Where(u => u.Code == "PC")
                .Select(u => u.Id)
                .FirstAsync());

        await factory.CreateUserAsync($"drpt-{suffix}-owner", Roles.Owner);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, $"drpt-{suffix}-owner");
        using HttpResponseMessage priced = await PostJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/catalog/products/{product.Value}/prices"),
            new { amount = 45m, reason = "Test launch price", locationId = store.Value },
            owner);
        priced.StatusCode.Should().Be(HttpStatusCode.Created, await priced.Content.ReadAsStringAsync());

        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, 100m, 30m);

        return new Seed(
            store, locationName, managerUserName, managerId, employeeCode, deviceCode, device, product, unitId, $"4900{suffix}0001");
    }

    // ---- Return/refund seeding ------------------------------------------

    /// <summary>
    /// Seeds a referenced return of 1 unit and a full cash refund directly via the
    /// public domain aggregate API. The report only reads the resulting rows.
    /// </summary>
    private static async Task SeedReturnAndRefundAsync(
        PosApiFactory factory,
        SaleId saleId,
        CashierShiftId shiftId,
        DeviceId deviceId)
    {
        await factory.WithServiceAsync(async context =>
        {
            Sale sale = await context.Sales
                .AsNoTracking()
                .Include(s => s.Items)
                .SingleAsync(s => s.Id == saleId);

            IReadOnlyDictionary<SaleItemId, decimal> alreadyReturned =
                sale.Items.ToDictionary(i => i.Id, _ => 0m);

            DateTimeOffset stamped = DateTimeOffset.UtcNow;

            Result<SalesReturn> createResult = SalesReturn.Create(
                DocumentNumber.FromTrustedSource("RET-2026-DRX-000001"),
                EventId.New(),
                saleId,
                sale.LocationId,
                shiftId,
                deviceId,
                customerId: null,
                sale.BusinessDate,
                stamped,
                sale.CompletedByUserId,
                [new ReturnItemSpec(sale.Items[0], 1m)],
                alreadyReturned);

            if (createResult.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Could not create test return: {string.Join("; ", createResult.Errors.Select(e => e.Code))}");
            }

            SalesReturn salesReturn = createResult.Value;

            IReadOnlyDictionary<PaymentMethod, decimal> paidByMethod = new Dictionary<PaymentMethod, decimal>
            {
                [PaymentMethod.Cash] = 90m,
            };

            Result<Refund> refundResult = salesReturn.IssueRefund(
                EventId.New(),
                shiftId,
                deviceId,
                PaymentMethod.Cash,
                45m,
                tendered: 45m,
                providerReference: null,
                stamped.AddMinutes(1),
                sale.CompletedByUserId,
                paidByMethod,
                priorRefundedByMethod: new Dictionary<PaymentMethod, decimal>(),
                cashRoundingIncrement: 0.01m);

            if (refundResult.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Could not create test refund: {string.Join("; ", refundResult.Errors.Select(e => e.Code))}");
            }

            // Add the return (cascade-adds the refund via navigation).
            context.SalesReturns.Add(salesReturn);
            await context.SaveChangesAsync();

            return true;
        });
    }

    // ---- Request helpers ------------------------------------------------

    private static async Task<Guid> CompleteAsync(
        HttpClient client,
        string accessToken,
        Seed seed,
        Guid shiftId,
        ProductId product,
        decimal quantity,
        string number,
        IReadOnlyList<(PaymentMethod Method, decimal Amount, decimal? Tendered, string? ProviderRef)> payments)
    {
        object body = new
        {
            number,
            eventId = Guid.CreateVersion7(),
            locationId = seed.Store.Value,
            cashierShiftId = shiftId,
            deviceId = seed.Device.Value,
            customerId = (Guid?)null,
            businessDate = BusinessDate,
            completedAtUtc = DateTimeOffset.UtcNow,
            lines = new[]
            {
                new
                {
                    productId = product.Value,
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
            payments = payments
                .Select(p => new
                {
                    method = p.Method,
                    amount = p.Amount,
                    tendered = p.Tendered,
                    providerReference = p.ProviderRef,
                })
                .ToArray(),
        };

        using HttpResponseMessage response = await PostJsonAsync(client, "/api/v1/sales", body, accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

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
                openingFloat = 100m,
            },
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonDocument> GetReportAsync(
        HttpClient client,
        string accessToken,
        LocationId locationId,
        DateOnly date)
    {
        using HttpResponseMessage response = await GetReportRawAsync(client, accessToken, locationId, date);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> GetReportRawAsync(
        HttpClient client,
        string accessToken,
        LocationId locationId,
        DateOnly date)
    {
        string path = FormattableString.Invariant(
            $"/api/v1/reports/daily-sales?locationId={locationId.Value}&date={date:yyyy-MM-dd}");

        return await GetAsync(client, path, accessToken);
    }

    private static string ShiftNumber(string deviceCode, long sequence)
        => DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, deviceCode, sequence).Value;

    private static async Task<string> PinSignInAsync(HttpClient client, string employeeCode, Guid deviceId)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/auth/login/pin", UriKind.Relative))
        {
            Content = JsonContent.Create(new { employeeCode, pin = PosApiFactory.TestPin }),
        };

        request.Headers.Add("X-Device-Id", deviceId.ToString("D", CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

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
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }

    private sealed record Seed(
        LocationId Store,
        string LocationName,
        string ManagerUserName,
        UserId ManagerId,
        string ManagerEmployeeCode,
        string DeviceCode,
        DeviceId Device,
        ProductId Product,
        UnitOfMeasureId UnitId,
        string Barcode)
    {
        public string SaleNumber(int sequence = 1) => DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, DeviceCode, sequence).Value;
    }
}