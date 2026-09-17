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
using Pos.Domain.Catalog;
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
    public async Task ASalePricedFromASupersededRow_IsKeptAtWhatWasCharged_AndTheDifferenceReported()
    {
        Seed seed = await SeedAsync("sp2", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        // Head office raised the price to 60 while the register was offline. The
        // device was still charging 45 a unit from the row it had cached, and
        // the customer paid 90 for two and walked out.
        Guid quoted = await factory.WithServiceAsync(context => context.Set<ProductPrice>()
            .AsNoTracking()
            .Where(p => p.ProductId == seed.Product)
            .Select(p => p.Id.Value)
            .FirstAsync());

        await RepriceAsync(client, seed, 60m);

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;

        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "SaleCompleted", SalePayload(
                seed, shiftId, saleNumber, quantity: 2m, netTotal: 90m, quotedPriceVersion: quoted)));

        results[1].GetProperty("outcome").GetString().Should().Be(
            "Accepted", "the goods are gone and the money is in the drawer");

        Sale sale = await factory.WithServiceAsync(context => context.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .SingleAsync(s => s.Number == saleNumber));

        // Recorded at what was charged, against the row it was charged from —
        // not re-priced to 60, and not marked as a manual override, which would
        // put an entry nobody authorized on the price-override report.
        sale.NetTotal.Should().Be(90m);
        sale.Items.Single().UnitPrice.Should().Be(45m);
        sale.Items.Single().PriceVersion.Value.Should().Be(quoted);
        sale.Items.Single().PriceWasOverridden.Should().BeFalse();

        bool reported = await factory.WithServiceAsync(context => context.AuditLog
            .AsNoTracking()
            .AnyAsync(e => e.Action == AuditActions.Sales.PriceVariance));

        reported.Should().BeTrue("the difference is reported, never silently absorbed");
    }

    [Fact]
    public async Task ALineQuotingAnotherProductsPriceRow_IsRefused()
    {
        Seed seed = await SeedAsync("sp7", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        // A cheap product's price row, aimed at an expensive product's line.
        ProductId other = await factory.CreateProductAsync("SY-sp7-P2", "Sync Product sp7b", "610sp7b0001");
        await RepriceAsync(client, seed, 5m, other);

        Guid foreign = await factory.WithServiceAsync(context => context.Set<ProductPrice>()
            .AsNoTracking()
            .Where(p => p.ProductId == other)
            .Select(p => p.Id.Value)
            .FirstAsync());

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;

        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "SaleCompleted", SalePayload(
                seed, shiftId, saleNumber, quantity: 1m, netTotal: 5m, quotedPriceVersion: foreign)));

        results[1].GetProperty("outcome").GetString().Should().Be("Rejected");
        results[1].GetProperty("errorCode").GetString().Should().Be(
            "sale.item.quoted_price_not_applicable",
            "naming one of our price rows is not the same as being priced by it");
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
    public async Task ASaleVoidedOffline_PutsTheStockBack_AndTheReprintIsLogged()
    {
        Seed seed = await SeedAsync("sp8", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;

        // A whole till session through an outage: open, sell, reprint the
        // customer's receipt, then void the sale when they change their mind.
        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "SaleCompleted", SalePayload(seed, shiftId, saleNumber, quantity: 2m, netTotal: 90m)),
            Event(3, "SaleReceiptReprinted", ReprintPayload(seed, saleNumber)),
            Event(4, "SaleVoided", VoidPayload(seed, shiftId, saleNumber)));

        results.Select(r => r.GetProperty("outcome").GetString())
            .Should().Equal("Accepted", "Accepted", "Accepted", "Accepted");

        Sale sale = await factory.WithServiceAsync(context => context.Sales
            .AsNoTracking()
            .SingleAsync(s => s.Number == saleNumber));

        sale.Status.Should().Be(SaleStatus.Voided);
        sale.VoidReason.Should().Be("Customer changed their mind");

        // The reversing post is the point: a void that only changed a status
        // would leave two units sold that are still on the shelf.
        decimal available = await factory.WithServiceAsync(context => context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == seed.Store
                        && b.ProductId == seed.Product
                        && b.State == InventoryState.Available)
            .Select(b => b.Quantity)
            .SumAsync());

        available.Should().Be(10m, "the goods came back");

        bool reprintLogged = await factory.WithServiceAsync(context => context.AuditLog
            .AsNoTracking()
            .AnyAsync(e => e.Action == AuditActions.Sales.ReceiptReprinted));

        reprintLogged.Should().BeTrue(
            "a second copy of a receipt can walk out of the shop and come back as a return");
    }

    [Fact]
    public async Task AVoidWhoseSaleTheServerRefused_IsRefusedTooRatherThanFloatingFree()
    {
        Seed seed = await SeedAsync("sp9", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;

        // The sale is not uploaded at all. A void that went through anyway would
        // put stock back on a shelf against nothing.
        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "SaleVoided", VoidPayload(seed, shiftId, saleNumber)));

        results[1].GetProperty("outcome").GetString().Should().Be("Rejected");
        results[1].GetProperty("errorCode").GetString().Should().Be("sync.sale_unknown");

        decimal available = await factory.WithServiceAsync(context => context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == seed.Store
                        && b.ProductId == seed.Product
                        && b.State == InventoryState.Available)
            .Select(b => b.Quantity)
            .SumAsync());

        available.Should().Be(10m, "nothing was invented to reverse");
    }

    [Fact]
    public async Task GoodsTakenBackOffline_ArePricedFromTheSaleTheServerHolds_AndTheCashIsAccountedFor()
    {
        Seed seed = await SeedAsync("spa", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        Guid shiftId = Guid.CreateVersion7();
        Guid returnId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string saleNumber = DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, seed.DeviceCode, 1).Value;
        string returnNumber = DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, seed.DeviceCode, 1).Value;

        // Sold three at 45, one comes back, 45 out of the drawer.
        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "SaleCompleted", SalePayload(seed, shiftId, saleNumber, quantity: 3m, netTotal: 135m)),
            Event(3, "SalesReturnCreated", ReturnPayload(seed, shiftId, returnId, returnNumber, saleNumber, 1m)),
            Event(4, "RefundIssued", RefundPayload(seed, shiftId, returnId, returnNumber, 45m)));

        results.Select(r => r.GetProperty("outcome").GetString())
            .Should().Equal("Accepted", "Accepted", "Accepted", "Accepted");

        SalesReturn taken = await factory.WithServiceAsync(context => context.SalesReturns
            .AsNoTracking()
            .Include(r => r.Items)
            .Include(r => r.Refunds)
            .SingleAsync(r => r.Number == returnNumber));

        // The device sent a product and a quantity, nothing more. The 45 is the
        // server's, matched against the sale line it holds.
        taken.Items.Should().ContainSingle();
        taken.Items.Single().Quantity.Should().Be(1m);
        taken.Items.Single().UnitPrice.Should().Be(45m);
        taken.RefundableTotal.Should().Be(45m);
        taken.Refunds.Should().ContainSingle();
        taken.Refunds.Single().Amount.Should().Be(45m);

        // Returned goods do not go back on the shelf: seven sellable, one held
        // for inspection (OFFLINE_SYNC.md §1).
        decimal available = await QuantityAsync(seed, InventoryState.Available);
        decimal pending = await QuantityAsync(seed, InventoryState.ReturnPending);

        available.Should().Be(7m);
        pending.Should().Be(1m, "somebody inspects it before it is sold again");
    }

    [Fact]
    public async Task ARefundForAReturnTheServerHasNotSeen_PaysOutNothing()
    {
        Seed seed = await SeedAsync("spb", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string returnNumber = DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, seed.DeviceCode, 1).Value;

        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "RefundIssued", RefundPayload(seed, shiftId, Guid.CreateVersion7(), returnNumber, 45m)));

        results[1].GetProperty("outcome").GetString().Should().Be("Rejected");
        results[1].GetProperty("errorCode").GetString().Should().Be("sync.return_unknown");

        bool anyRefund = await factory.WithServiceAsync(context => context.Refunds
            .AsNoTracking()
            .AnyAsync());

        anyRefund.Should().BeFalse("money is never recorded as leaving a drawer against nothing");
    }

    [Fact]
    public async Task ABlindReturnFromADevice_IsRefused()
    {
        Seed seed = await SeedAsync("spc", shelfQty: 10m);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        Guid shiftId = Guid.CreateVersion7();
        string shiftNumber = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, seed.DeviceCode, 1).Value;
        string returnNumber = DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, seed.DeviceCode, 1).Value;

        // sale.return_blind is not offline-capable, so it can never reach a
        // device's snapshot. A payload claiming one is a back door, not a case.
        IReadOnlyList<JsonElement> results = await PushAsync(client, token, seed,
            Event(1, "ShiftOpened", ShiftPayload(seed, shiftId, shiftNumber, ShiftStatus.Open)),
            Event(2, "SalesReturnCreated", ReturnPayload(
                seed, shiftId, Guid.CreateVersion7(), returnNumber, saleNumber: null, quantity: 1m)));

        results[1].GetProperty("outcome").GetString().Should().Be("Rejected");
        results[1].GetProperty("errorCode").GetString().Should().Be("sync.blind_return_not_permitted");
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
        UserId? cashier = null,
        Guid? quotedPriceVersion = null)
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
            [new SaleLineSyncPayload(
                seed.Product.Value, quantity, seed.UnitId.Value, seed.Barcode, null, null, 0m, null, quotedPriceVersion)],
            [new SalePaymentSyncPayload(nameof(PaymentMethod.Cash), netTotal, netTotal, null)]);

    private static SaleVoidSyncPayload VoidPayload(Seed seed, Guid shiftId, string saleNumber)
        => new(
            Guid.CreateVersion7(),
            saleNumber,
            seed.Store.Value,
            seed.Device.Value,
            shiftId,
            BusinessDate,
            seed.CashierId.Value,
            DateTimeOffset.UtcNow,
            "Customer changed their mind");

    private static ReceiptPrintSyncPayload ReprintPayload(Seed seed, string saleNumber)
        => new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            saleNumber,
            seed.Store.Value,
            seed.Device.Value,
            seed.CashierId.Value,
            DateTimeOffset.UtcNow,
            IsReprint: true,
            "Customer asked for another copy");

    private static SalesReturnSyncPayload ReturnPayload(
        Seed seed,
        Guid shiftId,
        Guid returnId,
        string returnNumber,
        string? saleNumber,
        decimal quantity)
        => new(
            returnId,
            returnNumber,
            Guid.CreateVersion7(),
            saleNumber,
            seed.Store.Value,
            seed.Device.Value,
            shiftId,
            null,
            seed.CashierId.Value,
            DateTimeOffset.UtcNow,
            BusinessDate,
            [new SalesReturnLineSyncPayload(seed.Product.Value, quantity)]);

    private static RefundSyncPayload RefundPayload(
        Seed seed,
        Guid shiftId,
        Guid returnId,
        string returnNumber,
        decimal amount)
        => new(
            Guid.CreateVersion7(),
            returnId,
            returnNumber,
            seed.Store.Value,
            seed.Device.Value,
            shiftId,
            nameof(PaymentMethod.Cash),
            amount,
            amount,
            null,
            DateTimeOffset.UtcNow,
            seed.CashierId.Value);

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

    private static async Task RepriceAsync(HttpClient client, Seed seed, decimal amount, ProductId? product = null)
    {
        string owner = await OwnerTokenAsync(client, seed);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"/api/v1/catalog/products/{(product ?? seed.Product).Value}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { amount, reason = "Sync test reprice", locationId = seed.Store.Value }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> OwnerTokenAsync(HttpClient client, Seed seed)
    {
        using HttpResponseMessage signedIn = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = seed.OwnerUserName, password = PosApiFactory.TestPassword },
            CancellationToken.None);
        signedIn.StatusCode.Should().Be(HttpStatusCode.OK, await signedIn.Content.ReadAsStringAsync());

        using JsonDocument doc = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private Task<decimal> QuantityAsync(Seed seed, InventoryState state)
        => factory.WithServiceAsync(context => context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == seed.Store && b.ProductId == seed.Product && b.State == state)
            .Select(b => b.Quantity)
            .SumAsync());

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

        // One character of the suffix becomes the device short code, and two
        // seeds sharing a code would enrol the same register twice. Asserting it
        // here turns a puzzling failure in an unrelated test into a plain one.
        suffix.Should().HaveLength(3, "the seed suffix contributes its last character to a unique device code");

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

        return new Seed(
            store, cashierId, employeeCode, deviceCode, device, product, unitId, $"6100{suffix}0001",
            $"sync-{suffix}-owner");
    }

    private sealed record Seed(
        LocationId Store,
        UserId CashierId,
        string EmployeeCode,
        string DeviceCode,
        DeviceId Device,
        ProductId Product,
        UnitOfMeasureId UnitId,
        string Barcode,
        string OwnerUserName);
}
