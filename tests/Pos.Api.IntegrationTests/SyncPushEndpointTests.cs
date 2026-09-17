using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// A register that traded through an outage, uploading what it did when the
/// link came back. The events go over real HTTP against the real container, so
/// what is tested is the whole path: the device's own payload shapes, the push
/// engine, and the appliers replaying the business through the same handlers
/// the online endpoints use.
/// </summary>
[Collection("api")]
public sealed class SyncPushEndpointTests(PosApiFactory factory)
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 15);

    [Fact]
    public async Task AShiftAndASaleRungUpOffline_LandCentrally_AtThePricesTheServerHolds()
    {
        Seed seed = await SeedAsync("sp1", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;

        // The device charged 40 a unit. The server holds 45 — see the variance
        // test below; here the device charged the price the server also holds.
        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "SaleCompleted", SalePayload(seed, shiftId, saleNumber, quantity: 2m, netTotal: 90m)));

        results.Should().HaveCount(2);
        results[0].GetProperty("outcome").GetString().Should().Be("Accepted");
        results[1].GetProperty("outcome").GetString().Should().Be("Accepted");
        results[1].GetProperty("serverDocumentNumber").GetString().Should().Be(saleNumber);

        // The sale exists under the number printed at the till, priced from the
        // rows the server holds rather than from anything the device sent.
        Sale sale = await factory.WithServiceAsync(context => context.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .SingleAsync(s => s.Number == saleNumber));

        sale.LocationId.Should().Be(seed.Store);
        sale.CompletedByUserId.Should().Be(seed.CashierId);
        sale.NetTotal.Should().Be(90m);
        sale.Items.Should().ContainSingle();
        sale.Items.Single().UnitPrice.Should().Be(45m, "the server re-resolves the price from its own rows");
        sale.Items.Single().Quantity.Should().Be(2m);
        sale.Payments.Should().ContainSingle();

        // And the goods left the shelf: the ledger posted, not just the record.
        decimal available = await factory.WithServiceAsync(context => context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == seed.Store
                        && b.ProductId == seed.Product
                        && b.State == InventoryState.Available)
            .Select(b => b.Quantity)
            .SumAsync());

        available.Should().Be(8m, "a sale that syncs moves stock, it does not just file paperwork");
    }

    [Fact]
    public async Task ASaleSettledAtAPriceTheServerDoesNotHold_IsRefusedForNow_NotSilentlyRePriced()
    {
        Seed seed = await SeedAsync("sp2", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;

        // The device's cached price was stale: it charged 80 for two where the
        // server's effective row now says 90. The customer paid 80 and left.
        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "SaleCompleted", SalePayload(seed, shiftId, saleNumber, quantity: 2m, netTotal: 80m)));

        // This is a known gap, pinned here so it cannot change unnoticed.
        // OFFLINE_SYNC.md §7 says such a sale is accepted at the price actually
        // charged with the difference noted, and the server currently refuses it
        // instead: it re-prices the line to 90, and the 80 that was tendered no
        // longer settles the sale. Closing it means carrying the price row the
        // device quoted, so the line is recorded against that version rather
        // than re-priced or dressed up as a manual override.
        //
        // The record is parked, not destroyed: the device escalates a refused
        // event as a SyncFailure and keeps it forever (OFFLINE_SYNC.md §3.2).
        results[1].GetProperty("outcome").GetString().Should().Be("Rejected");
        results[1].GetProperty("errorCode").GetString().Should().Be(
            "sale.payment_mismatch", "the refusal names the re-pricing, not the sale");

        bool held = await factory.WithServiceAsync(context => context.Sales
            .AsNoTracking()
            .AnyAsync(s => s.Number == saleNumber));

        held.Should().BeFalse("nothing half-applied: the refusal rolled its transaction back");
    }

    [Fact]
    public async Task ASaleRungUpByACashierSinceUnassigned_StillLands_AndIsFlaggedForReview()
    {
        Seed seed = await SeedAsync("sp6", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        // The cashier who rang the sale up has no assignment at this store any
        // more, so sale.create does not evaluate for them here. Somebody else is
        // signed in when the register finally reaches head office, which is the
        // only reason the batch can be uploaded at all.
        UserId departed = await factory.CreateUserAsync(
            $"sync-sp6-departed", Roles.Cashier, locations: []);

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;

        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open, departed)),
            Event(2, "SaleCompleted", SalePayload(
                seed, shiftId, saleNumber, quantity: 1m, netTotal: 45m, cashier: departed)));

        results[1].GetProperty("outcome").GetString().Should().Be(
            "RequiresReview", "the goods left the shelf and the money changed hands; a person decides what it means");
        results[1].GetProperty("errorCode").GetString().Should().Be("sync.cashier_permission_withdrawn");

        bool held = await factory.WithServiceAsync(context => context.Sales
            .AsNoTracking()
            .AnyAsync(s => s.Number == saleNumber));

        held.Should().BeTrue("flagged is not refused: the sale is recorded either way");

        bool flagged = await factory.WithServiceAsync(context => context.AuditLog
            .AsNoTracking()
            .AnyAsync(e => e.Action == AuditActions.Sync.RequiresReview));

        flagged.Should().BeTrue("and whoever reviews it can find out why it was flagged");
    }

    [Fact]
    public async Task ASaleForAShiftTheServerHasNotSeen_IsDeferredWithEverythingBehindIt()
    {
        Seed seed = await SeedAsync("sp3", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        Guid shiftId = Guid.CreateVersion7();
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;

        // Sequence 1 never arrived, so the sale is not applied against a state
        // the device never had — it waits for the shift that precedes it.
        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(2, "SaleCompleted", SalePayload(seed, shiftId, saleNumber, quantity: 1m, netTotal: 45m)));

        results[0].GetProperty("outcome").GetString().Should().Be("Deferred");

        bool held = await factory.WithServiceAsync(context => context.Sales
            .AsNoTracking()
            .AnyAsync(s => s.Number == saleNumber));

        held.Should().BeFalse();
    }

    [Fact]
    public async Task TheSameReceiptUploadedUnderTwoEventIds_IsRefusedTheSecondTime()
    {
        Seed seed = await SeedAsync("sp4", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;

        IReadOnlyList<JsonElement> first = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "SaleCompleted", SalePayload(seed, shiftId, saleNumber, quantity: 1m, netTotal: 45m)));

        first.Select(r => r.GetProperty("outcome").GetString()).Should().Equal("Accepted", "Accepted");

        // A fresh event identifier carrying a receipt the server already holds.
        // The idempotency record cannot catch this one; the number does.
        IReadOnlyList<JsonElement> again = await PushAsync(client, token, seed,
            Event(3, "SaleCompleted", SalePayload(seed, shiftId, saleNumber, quantity: 1m, netTotal: 45m)));

        again[0].GetProperty("outcome").GetString().Should().Be("Rejected");
        again[0].GetProperty("errorCode").GetString().Should().Be("sync.sale_already_held");

        int sales = await factory.WithServiceAsync(context => context.Sales
            .AsNoTracking()
            .CountAsync(s => s.Number == saleNumber));

        sales.Should().Be(1, "one receipt, one sale, whatever the device retries");
    }

    [Fact]
    public async Task ABatchNamingAnotherDevice_IsRefusedBeforeAnythingIsRead()
    {
        Seed seed = await SeedAsync("sp5", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/sync/push", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                deviceId = Guid.CreateVersion7(),
                batchId = Guid.CreateVersion7(),
                clientSentAtUtc = DateTimeOffset.UtcNow,
                deviceUptimeTicks = 1_000L,
                events = Array.Empty<object>(),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static ShiftSyncPayload ShiftPayload(
        Seed seed,
        Guid shiftId,
        string number,
        ShiftStatus status,
        UserId? cashier = null)
        => new(
            shiftId,
            number,
            seed.Store.Value,
            seed.Device.Value,
            (cashier ?? seed.CashierId).Value,
            100m,
            BusinessDate,
            DateTimeOffset.UtcNow,
            status.ToString());

    private static SaleSyncPayload SalePayload(
        Seed seed,
        Guid shiftId,
        string number,
        decimal quantity,
        decimal netTotal,
        UserId? cashier = null)
        => new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            number,
            seed.Store.Value,
            seed.Device.Value,
            shiftId,
            (cashier ?? seed.CashierId).Value,
            null,
            netTotal,
            DateTimeOffset.UtcNow,
            BusinessDate,
            [new SaleLineSyncPayload(seed.Product.Value, quantity, seed.UnitId.Value, seed.Barcode, null, null, 0m, null)],
            [new SalePaymentSyncPayload(nameof(PaymentMethod.Cash), netTotal, netTotal, null)]);

    private static object Event<TPayload>(long sequence, string type, TPayload payload)
    {
        string json = CanonicalJson.Serialize(payload);

        return new
        {
            eventId = Guid.CreateVersion7(),
            deviceSequence = sequence,
            type,
            occurredAtUtc = DateTimeOffset.UtcNow,
            payloadJson = json,
            payloadHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
        };
    }

    private static async Task<IReadOnlyList<JsonElement>> PushAsync(
        HttpClient client,
        string token,
        Seed seed,
        params object[] events)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/sync/push", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                deviceId = seed.Device.Value,
                batchId = Guid.CreateVersion7(),
                clientSentAtUtc = DateTimeOffset.UtcNow,
                deviceUptimeTicks = 1_000L,
                events,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "every event is answered for individually, including a refused one: " + body);

        using JsonDocument doc = JsonDocument.Parse(body);
        return [.. doc.RootElement.GetProperty("results").EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<string> SignInAsync(HttpClient client, Seed seed)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/auth/login/pin", UriKind.Relative))
        {
            Content = JsonContent.Create(new { employeeCode = seed.EmployeeCode, pin = PosApiFactory.TestPin }),
        };
        request.Headers.Add(
            "X-Device-Id",
            seed.Device.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<Seed> SeedAsync(string suffix, decimal shelfQty)
    {
        LocationId store = await factory.CreateLocationAsync($"SY-{suffix}", $"Sync Store {suffix}", LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalCustomer);

        string employeeCode = $"sy{suffix}";
        UserId cashierId = await factory.CreateUserAsync(
            $"sync-{suffix}-sm", Roles.StoreManager, locations: [store], employeeCode: employeeCode);

        string deviceCode = $"SY{suffix[^1]}";
        DeviceId device = await factory.CreateDeviceAsync(deviceCode, store);

        ProductId product = await factory.CreateProductAsync(
            $"SY-{suffix}-P1", $"Sync Product {suffix}", $"6100{suffix}0001");

        UnitOfMeasureId unitId = await factory.WithServiceAsync(context => context.UnitsOfMeasure
            .AsNoTracking()
            .Where(u => u.Code == "PC")
            .Select(u => u.Id)
            .FirstAsync());

        await factory.CreateUserAsync($"sync-{suffix}-owner", Roles.Owner);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage signedIn = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = $"sync-{suffix}-owner", password = PosApiFactory.TestPassword },
            CancellationToken.None);
        signedIn.StatusCode.Should().Be(HttpStatusCode.OK, await signedIn.Content.ReadAsStringAsync());

        using JsonDocument ownerDoc = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync());
        string owner = ownerDoc.RootElement.GetProperty("accessToken").GetString()!;

        using HttpRequestMessage priceRequest = new(
            HttpMethod.Post,
            new Uri($"/api/v1/catalog/products/{product.Value}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { amount = 45m, reason = "Sync test price", locationId = store.Value }),
        };
        priceRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);

        using HttpResponseMessage priced = await client.SendAsync(priceRequest, CancellationToken.None);
        priced.StatusCode.Should().Be(HttpStatusCode.Created, await priced.Content.ReadAsStringAsync());

        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, shelfQty, 30m);

        return new Seed(store, cashierId, employeeCode, deviceCode, device, product, unitId, $"6100{suffix}0001");
    }

    private sealed record Seed(
        LocationId Store,
        UserId CashierId,
        string EmployeeCode,
        string DeviceCode,
        DeviceId Device,
        ProductId Product,
        UnitOfMeasureId UnitId,
        string Barcode);
}
