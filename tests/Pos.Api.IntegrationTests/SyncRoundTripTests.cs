using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Persistence;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// A register's whole day, through the real server over real HTTP: it downloads
/// its catalogue, loses the line, trades, comes back, uploads what it did, and
/// hears about what changed while it was away.
/// </summary>
/// <remarks>
/// Every piece of this has its own tests. What this one covers is the joins
/// between them, which is where a system like this actually breaks: the pieces
/// were each right and nobody had ever run one into the next.
/// </remarks>
[Collection("api")]
public sealed partial class SyncRoundTripTests(PosApiFactory factory) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 2, 0, 0, TimeSpan.Zero);

    private string directory = null!;

    public Task InitializeAsync()
    {
        this.directory = Path.Combine(Path.GetTempPath(), "vaultflow-roundtrip", Guid.NewGuid().ToString("N"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ARegisterDownloadsItsCatalogue_TradesThroughAnOutage_ThenUploadsAndCatchesUp()
    {
        Seed seed = await SeedAsync("rt1");
        using HttpClient client = factory.CreateClient();

        // Signing in by PIN is what puts this cashier's offline authority on the
        // register's feed. Without it the register can download a catalogue and
        // still refuse to sell, because it has nothing to answer a permission
        // question with once the line is gone.
        string token = await PinSignInAsync(client, seed);

        await using Device register = await Device.StartAsync(this.directory, factory, token, seed);

        // ---- 1. Download. The register starts with nothing at all. ----
        ChangeFeedDownloadOutcome downloaded = await register.Downloader.RunAsync(CancellationToken.None);

        downloaded.Problem.Should().BeNull();
        downloaded.RebaselineRequired.Should().BeFalse();
        downloaded.Unreachable.Should().BeFalse();
        downloaded.Applied.Should().BePositive();

        await using (PosDeviceDbContext store = register.OpenStore())
        {
            (await store.Products.AnyAsync(p => p.Id == seed.Product, CancellationToken.None))
                .Should().BeTrue("it cannot ring up what it has never heard of");
            (await store.ProductPrices.AnyAsync(p => p.ProductId == seed.Product, CancellationToken.None))
                .Should().BeTrue();
            List<string> codes = await store.Locations
                .AsNoTracking()
                .Select(l => l.Code + ":" + l.Kind)
                .ToListAsync(CancellationToken.None);

            codes.Should().Contain(
                c => c.StartsWith(seed.StoreCode, StringComparison.Ordinal),
                "the VAT rate and rounding come down with the store");

            // Every register posts the other leg of a sale against the external
            // counterparty, so it is everybody's business, not one store's.
            string served = await factory.WithServiceAsync(context => context.ChangeFeed
                .AsNoTracking()
                .Where(e => e.Kind == "LocationChanged" && e.PayloadJson.Contains(SystemLocationCodes.ExternalCustomer))
                .Select(e => e.Sequence + "/" + (e.LocationScopeId == null ? "global" : "scoped"))
                .FirstOrDefaultAsync()) ?? "no feed row at all";

            codes.Should().Contain(
                c => c.StartsWith(SystemLocationCodes.ExternalCustomer, StringComparison.Ordinal),
                "device has {0}; server feed row is {1}", string.Join(", ", codes), served);
            (await store.PermissionSnapshots.AnyAsync(CancellationToken.None))
                .Should().BeTrue("and the authority to sell, which expires");
        }

        // ---- 2. Stock, so there is something on the shelf to sell. ----
        await register.ReceiveStockAsync(seed, 10m);

        // ---- 3. The line goes down, and the shop keeps trading. ----
        DocumentNumber shiftNumber = await register.NextNumberAsync(DocumentType.CashierShift);
        Result<CashierShiftId> opened = await register.SendAsync(
            new OpenShiftCommand(
                shiftNumber, seed.Store, DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime), 2000m));
        opened.IsSuccess.Should().BeTrue(opened.IsFailure ? opened.Error.ToString() : string.Empty);

        Result<SaleId> sold = await register.SellAsync(seed, opened.Value, quantity: 2m);
        sold.IsSuccess.Should().BeTrue(sold.IsFailure ? sold.Error.ToString() : string.Empty);

        // ---- 4. The line comes back, and the register empties its outbox. ----
        SyncUploadOutcome uploaded = await register.Uploader.RunOnceAsync(CancellationToken.None);

        uploaded.Unreachable.Should().BeFalse();
        uploaded.Escalated.Should().Be(0);
        uploaded.Accepted.Should().Be(2, "the shift and the sale");

        string saleNumber = await register.SaleNumberAsync(sold.Value);

        Sale central = await factory.WithServiceAsync(context => context.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .SingleAsync(s => s.Number == saleNumber));

        central.Items.Should().ContainSingle();
        central.Items.Single().Quantity.Should().Be(2m);
        central.Items.Single().UnitPrice.Should().Be(45m, "the server priced it from its own rows");

        decimal shelf = await factory.WithServiceAsync(context => context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == seed.Store
                        && b.ProductId == seed.Product
                        && b.State == InventoryState.Available)
            .Select(b => b.Quantity)
            .SumAsync());

        shelf.Should().Be(8m, "the goods left the shelf centrally too, not just on the register");

        // ---- 5. Head office raises the price while nobody is looking. ----
        await RepriceAsync(client, seed, 60m);

        ChangeFeedDownloadOutcome caughtUp = await register.Downloader.RunAsync(CancellationToken.None);
        caughtUp.Applied.Should().BePositive();

        await using (PosDeviceDbContext store = register.OpenStore())
        {
            List<decimal> prices = await store.ProductPrices
                .AsNoTracking()
                .Where(p => p.ProductId == seed.Product)
                .Select(p => p.Amount)
                .ToListAsync(CancellationToken.None);

            prices.Should().Contain(60m, "the till charges what head office decided while it was away");
        }
    }

    [Fact]
    public async Task ARegisterWithACursorTheServerCannotHonour_IsToldToStartAgain()
    {
        Seed seed = await SeedAsync("rt2");
        using HttpClient client = factory.CreateClient();
        string token = await PinSignInAsync(client, seed);

        await using Device register = await Device.StartAsync(this.directory, factory, token, seed);

        // A first download, so the register holds a cursor it believes in.
        ChangeFeedDownloadOutcome first = await register.Downloader.RunAsync(CancellationToken.None);
        first.Problem.Should().BeNull();
        first.Cursor.Should().BePositive();

        // Things happen, and then the earliest of them is pruned, leaving a hole
        // between the register's cursor and what the server still holds. A page
        // with a hole in it would leave the register quietly wrong about its own
        // catalogue, so the server refuses the cursor instead.
        await RepriceAsync(client, seed, 55m);
        await RepriceAsync(client, seed, 65m);

        await factory.WithServiceAsync(context => context.ChangeFeed
            .Where(e => e.Sequence <= first.Cursor + 1)
            .ExecuteDeleteAsync());

        ChangeFeedDownloadOutcome outcome = await register.Downloader.RunAsync(CancellationToken.None);

        outcome.RebaselineRequired.Should().BeTrue();
        outcome.Unreachable.Should().BeFalse(
            "a cursor the server cannot honour is not a connection problem, and retrying would never fix it");
        outcome.Applied.Should().Be(0);
    }

    private static async Task<string> PinSignInAsync(HttpClient client, Seed seed)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/auth/login/pin", UriKind.Relative))
        {
            Content = JsonContent.Create(new { employeeCode = seed.EmployeeCode, pin = PosApiFactory.TestPin }),
        };
        request.Headers.Add(
            "X-Device-Id",
            seed.DeviceId.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task RepriceAsync(HttpClient client, Seed seed, decimal amount)
    {
        using HttpResponseMessage signedIn = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = seed.OwnerUserName, password = PosApiFactory.TestPassword },
            CancellationToken.None);
        signedIn.StatusCode.Should().Be(HttpStatusCode.OK, await signedIn.Content.ReadAsStringAsync());

        using JsonDocument doc = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync());
        string owner = doc.RootElement.GetProperty("accessToken").GetString()!;

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"/api/v1/catalog/products/{seed.Product.Value}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { amount, reason = "Round-trip reprice", locationId = seed.Store.Value }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);

        using HttpResponseMessage priced = await client.SendAsync(request, CancellationToken.None);
        priced.StatusCode.Should().Be(HttpStatusCode.Created, await priced.Content.ReadAsStringAsync());
    }

    private async Task<Seed> SeedAsync(string suffix)
    {
        LocationId store = await factory.CreateLocationAsync($"RT-{suffix}", $"Round Trip {suffix}", LocationKind.Store);
        LocationId counterparty = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalCustomer);

        // The feed carries changes, not a starting state. Anything that existed
        // before the feed did — EXT-CUSTOMER is provisioned at start-up — has no
        // row for a new register to read, so it is touched here to produce one.
        // That is the gap `/api/v1/sync/baseline` exists to fill, and this is a
        // test standing in for it rather than a thing the system does.
        await factory.WithServiceAsync<PosDbContext>(async context =>
        {
            Location external = await context.Locations.AsTracking().SingleAsync(l => l.Id == counterparty);

            // Marked modified rather than actually changed: nothing about it
            // needs to differ, it just has to pass through a save so the feed
            // records it once.
            context.Entry(external).State = EntityState.Modified;
            await context.SaveChangesAsync();
        });

        string employeeCode = $"rt{suffix[^1]}";
        UserId cashier = await factory.CreateUserAsync(
            $"rt-{suffix}-sm", Roles.StoreManager, locations: [store], employeeCode: employeeCode);

        string deviceCode = $"RT{suffix[^1]}";
        DeviceId device = await factory.CreateDeviceAsync(deviceCode, store);

        ProductId product = await factory.CreateProductAsync(
            $"RT-{suffix}-P1", $"Round Trip Product {suffix}", $"8300{suffix}0001");

        UnitOfMeasureId unit = await factory.WithServiceAsync(context => context.UnitsOfMeasure
            .AsNoTracking()
            .Where(u => u.Code == "PC")
            .Select(u => u.Id)
            .FirstAsync());

        await factory.CreateUserAsync($"rt-{suffix}-owner", Roles.Owner);

        // Stock is put on both shelves. Balances are not in the change feed — a
        // register keeps its own ledger and the server keeps the real one — so a
        // sale that the device could cover has to be coverable centrally too, or
        // the replay lands flagged for taking the shelf below zero. That the
        // flag fires correctly is its own test; here it would only be noise.
        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, 10m, 30m);

        Seed seed = new(
            store, $"RT-{suffix}".ToUpperInvariant(), cashier, employeeCode, deviceCode, device, product, unit,
            $"rt-{suffix}-owner");

        using HttpClient client = factory.CreateClient();
        await RepriceAsync(client, seed, 45m);

        return seed;
    }

    private sealed record Seed(
        LocationId Store,
        string StoreCode,
        UserId Cashier,
        string EmployeeCode,
        string DeviceCode,
        DeviceId DeviceId,
        ProductId Product,
        UnitOfMeasureId Unit,
        string OwnerUserName);
}
