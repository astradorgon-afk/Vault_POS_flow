using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Api.Endpoints;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// C9: the returns HTTP surface through the real pipeline — referenced and
/// blind returns, and referenced/blind refunds, with their cap refusals.
/// </summary>
[Collection("api")]
public sealed class PosReturnsEndpointsTests(PosApiFactory factory)
{
    [Theory]
    [InlineData(ReturnDispositionKind.Restock, InventoryState.Available, "c9f", false)]
    [InlineData(ReturnDispositionKind.Quarantine, InventoryState.Quarantine, "c9g", true)]
    [InlineData(ReturnDispositionKind.Damaged, InventoryState.Damaged, "c9h", false)]
    [InlineData(ReturnDispositionKind.SupplierReturn, InventoryState.Damaged, "c9i", true)]
    [InlineData(ReturnDispositionKind.Waste, InventoryState.External, "c9j", false)]
    public async Task Disposition_RoutesGoodsOnce_AndEnforcesReturnCap(
        ReturnDispositionKind kind, InventoryState target, string suffix, bool blind)
    {
        Seed seed = await SeedAsync(suffix, shelfQty: 5m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.ManagerUserName);
        string deviceToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, deviceToken, seed);
        Guid returnId = await CreateDispositionReturnAsync(client, deviceToken, seed, shiftId, blind);
        LocationId targetLocation = kind == ReturnDispositionKind.Waste
            ? await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff)
            : seed.Store;
        decimal before = await InventoryControlTestSupport.QuantityAsync(factory, targetLocation, seed.Product, target);
        var body = new DisposeSalesReturnBody(Guid.CreateVersion7(), seed.Store.Value, 1, 0.6m,
            kind, AdjustmentReasonCode.Damaged, "Inspected returned goods");
        string route = $"/api/v1/returns/{returnId}/disposition";

        using HttpResponseMessage first = await PostJsonAsync(client, route, body, token);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        using HttpResponseMessage replay = await PostJsonAsync(client, route, body, token);
        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        using HttpResponseMessage changedReplay = await PostJsonAsync(client, route, body with { Quantity = 0.4m }, token);
        changedReplay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(changedReplay)).Should().Be(ReturnDispositionErrors.EventConflict.Code);
        using HttpResponseMessage excess = await PostJsonAsync(client, route, body with { EventId = Guid.CreateVersion7() }, token);
        excess.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(excess)).Should().Be(ReturnDispositionErrors.QuantityExceeded.Code);

        (await InventoryControlTestSupport.QuantityAsync(factory, targetLocation, seed.Product, target)).Should().Be(before + 0.6m);
        (await InventoryControlTestSupport.QuantityAsync(factory, seed.Store, seed.Product, InventoryState.ReturnPending)).Should().Be(0.4m);
        await factory.WithServiceAsync(async context =>
        {
            var decisions = await context.Set<SalesReturnDisposition>().Where(d => d.SalesReturnId == new SalesReturnId(returnId)).ToListAsync();
            decisions.Should().ContainSingle();
            decisions[0].Quantity.Should().Be(0.6m);
            var movements = await context.InventoryMovements.Where(m => m.EventId == new EventId(body.EventId)).ToListAsync();
            movements.Should().HaveCount(2);
            movements.Sum(m => m.QuantityDelta).Should().Be(0m);
            (await context.SalesReturnItems.SingleAsync(i => i.SalesReturnId == new SalesReturnId(returnId)))
                .DispositionedQuantity.Should().Be(0.6m);
            var incidents = await context.QuarantineIncidents.Include(i => i.Lines).Where(i => i.LocationId == seed.Store).ToListAsync();
            incidents.Should().HaveCount(kind == ReturnDispositionKind.Quarantine ? 1 : 0);
            if (kind == ReturnDispositionKind.Quarantine)
            {
                incidents[0].Lines[0].ProductId.Should().Be(seed.Product);
                incidents[0].Lines[0].Quantity.Should().Be(0.6m);
            }
            return true;
        });
    }

    [Fact]
    public async Task Disposition_LedgerRefusal_RollsBackDecisionAndPendingQuantity()
    {
        Seed seed = await SeedAsync("c9k", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.ManagerUserName);
        string deviceToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, deviceToken, seed);
        Guid returnId = await CreateDispositionReturnAsync(client, deviceToken, seed, shiftId, blind: true);
        await InventoryControlTestSupport.SetBucketAsync(factory, seed.Store, seed.Product, 0m, 30m, InventoryState.ReturnPending);
        var body = new DisposeSalesReturnBody(Guid.CreateVersion7(), seed.Store.Value, 1, 1m,
            ReturnDispositionKind.Restock, AdjustmentReasonCode.CountCorrection, "Inspected");
        using HttpResponseMessage response = await PostJsonAsync(client, $"/api/v1/returns/{returnId}/disposition", body, token);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("inventory.insufficient_stock");
        await factory.WithServiceAsync(async context =>
        {
            (await context.Set<SalesReturnDisposition>().AnyAsync(d => d.Id == new EventId(body.EventId))).Should().BeFalse();
            (await context.SalesReturnItems.SingleAsync(i => i.SalesReturnId == new SalesReturnId(returnId)))
                .DispositionedQuantity.Should().Be(0m);
            (await context.InventoryMovements.AnyAsync(m => m.EventId == new EventId(body.EventId))).Should().BeFalse();
            return true;
        });
    }

    [Fact]
    public async Task Disposition_RequiresPermissionAndMatchingLocation()
    {
        Seed seed = await SeedAsync("c9l", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.ManagerUserName);
        string deviceToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, deviceToken, seed);
        Guid returnId = await CreateDispositionReturnAsync(client, deviceToken, seed, shiftId, blind: true);
        var body = new DisposeSalesReturnBody(Guid.CreateVersion7(), seed.Store.Value, 1, 1m,
            ReturnDispositionKind.Restock, AdjustmentReasonCode.CountCorrection, "Inspected");
        string route = $"/api/v1/returns/{returnId}/disposition";
        await factory.CreateUserAsync("disposition-cashier", Roles.Cashier, locations: [seed.Store]);
        string cashier = await SignInAsync(client, "disposition-cashier");
        using HttpResponseMessage denied = await PostJsonAsync(client, route, body, cashier);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        LocationId other = await factory.CreateLocationAsync("DISP-OTHER", "Other store", LocationKind.Store);
        await factory.CreateUserAsync("disposition-other", Roles.StoreManager, locations: [other]);
        string otherManager = await SignInAsync(client, "disposition-other");
        using HttpResponseMessage outside = await PostJsonAsync(client, route, body, otherManager);
        outside.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage forged = await PostJsonAsync(client, route, body with { LocationId = other.Value }, otherManager);
        forged.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(forged)).Should().Be(ReturnDispositionErrors.LocationMismatch.Code);
        using HttpResponseMessage invalid = await PostJsonAsync(client, route, body with { Quantity = 0.0001m }, token);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<Guid> CreateDispositionReturnAsync(HttpClient client, string token, Seed seed, Guid shiftId, bool blind)
    {
        Guid? saleId = blind ? null : await CompleteSaleAsync(client, token, seed, shiftId, quantity: 2m, seq: 1);
        using HttpResponseMessage response = await PostJsonAsync(client, blind ? "/api/v1/returns/blind" : "/api/v1/returns", new
        {
            number = DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, seed.DeviceCode, 1).Value,
            eventId = Guid.CreateVersion7(), saleId, locationId = seed.Store.Value, shiftId, deviceId = seed.Device.Value,
            businessDate = new DateOnly(2026, 9, 15), returnedAtUtc = DateTimeOffset.UtcNow,
            reason = "Customer returned goods for inspection",
            lines = new[] { new { productId = seed.Product.Value, quantity = 1m } },
        }, token);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadIdAsync(response);
    }

    [Fact]
    public async Task PartialRefunds_AcrossTwoReturns_ReachOriginalPayment_AndReplaySafely()
    {
        Seed seed = await SeedAsync("c9e", shelfQty: 5m);
        using HttpClient client = factory.CreateClient();
        string accessToken = await SignInManagerAsync(client, seed);
        Guid shiftId = await OpenShiftAsync(client, accessToken, seed);
        Guid saleId = await CompleteSaleAsync(client, accessToken, seed, shiftId, quantity: 2m, seq: 1);

        for (int sequence = 1; sequence <= 2; sequence++)
        {
            using HttpResponseMessage returned = await PostJsonAsync(client, "/api/v1/returns", new
            {
                number = DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, seed.DeviceCode, sequence).Value,
                eventId = Guid.CreateVersion7(),
                saleId,
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                businessDate = new DateOnly(2026, 9, 15),
                returnedAtUtc = DateTimeOffset.UtcNow,
                lines = new[] { new { productId = seed.Product.Value, quantity = 1m } },
            }, accessToken);
            returned.StatusCode.Should().Be(HttpStatusCode.Created, await returned.Content.ReadAsStringAsync());
            Guid returnId = await ReadIdAsync(returned);

            foreach (decimal amount in new[] { 30m, 15m })
            {
                var body = new
                {
                    saleId,
                    eventId = Guid.CreateVersion7(),
                    locationId = seed.Store.Value,
                    shiftId,
                    deviceId = seed.Device.Value,
                    method = 1,
                    amount,
                    tendered = amount,
                    refundedAtUtc = DateTimeOffset.UtcNow,
                };
                using HttpResponseMessage refund = await PostJsonAsync(
                    client, $"/api/v1/returns/{returnId}/refund", body, accessToken);
                refund.StatusCode.Should().Be(HttpStatusCode.OK, await refund.Content.ReadAsStringAsync());
                Guid refundId = await ReadIdAsync(refund);

                using HttpResponseMessage replay = await PostJsonAsync(
                    client, $"/api/v1/returns/{returnId}/refund", body, accessToken);
                replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
                (await ReadIdAsync(replay)).Should().Be(refundId);
            }

            using HttpResponseMessage excess = await PostJsonAsync(client, $"/api/v1/returns/{returnId}/refund", new
            {
                saleId,
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                shiftId,
                deviceId = seed.Device.Value,
                method = 1,
                amount = 0.01m,
                tendered = 0.01m,
                refundedAtUtc = DateTimeOffset.UtcNow,
            }, accessToken);
            excess.StatusCode.Should().Be(HttpStatusCode.Conflict, await excess.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(excess)).Should().Be(sequence == 1
                ? "sale.refund.exceeds_refundable"
                : "sale.refund.exceeds_paid_for_method");
        }

        await factory.WithServiceAsync(async context =>
        {
            var refunds = await (
                from refund in context.Refunds
                join salesReturn in context.SalesReturns on refund.SalesReturnId equals salesReturn.Id
                where salesReturn.SaleId == new SaleId(saleId)
                select refund).ToListAsync();
            refunds.Should().HaveCount(4);
            refunds.Sum(refund => refund.Amount).Should().Be(90m);
            return true;
        });
    }

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

        // A second refund exceeds this return's value, even though the sale's
        // original cash payment would still cover it.
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
        (await ReadErrorCodeAsync(ref2)).Should().Be("sale.refund.exceeds_refundable");
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
