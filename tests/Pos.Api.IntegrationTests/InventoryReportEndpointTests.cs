using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Locations;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The inventory reports of ROADMAP §Phase 15, over real HTTP. The roll-up is
/// pinned by the repository's own tests; what is tested here is the line the
/// permissions draw — a shelf count is operational, what the shelf cost is not.
/// </summary>
[Collection("api")]
public sealed class InventoryReportEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task StockroomStaffSeeTheShelf_ButNotWhatItCost()
    {
        LocationId store = await factory.CreateLocationAsync("IR-S1", "Inventory Report Store", LocationKind.Store);
        ProductId product = await factory.CreateProductAsync("IR-P1", "Report Biscuits", "7100000001");
        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, 12m, 30m);

        await factory.CreateUserAsync("ir-staff", Roles.InventoryStaff, locations: [store]);

        using HttpClient client = factory.CreateClient();
        string staff = await SignInAsync(client, "ir-staff");

        using HttpResponseMessage onHand = await GetAsync(
            client, $"/api/v1/reports/inventory/on-hand?locationId={store.Value}", staff);

        onHand.StatusCode.Should().Be(HttpStatusCode.OK, await onHand.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await onHand.Content.ReadAsStringAsync());
        JsonElement row = document.RootElement.EnumerateArray()
            .Single(r => r.GetProperty("sku").GetString() == "IR-P1");

        row.GetProperty("available").GetDecimal().Should().Be(12m);
        row.GetProperty("onHand").GetDecimal().Should().Be(12m);

        // No money anywhere in the shape. A shelf count that quietly discloses
        // cost is an operational report only head office may open.
        row.TryGetProperty("totalValue", out _).Should().BeFalse();
        row.TryGetProperty("averageUnitCost", out _).Should().BeFalse();

        // And the valuation, which is the same rows with the money on, is refused.
        using HttpResponseMessage valuation = await GetAsync(
            client, $"/api/v1/reports/inventory/valuation?locationId={store.Value}", staff);

        valuation.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AStoreManagerSeesTheValuationOfTheirOwnStore_AndNotAnother()
    {
        LocationId mine = await factory.CreateLocationAsync("IR-S2", "Inventory Report Mine", LocationKind.Store);
        LocationId theirs = await factory.CreateLocationAsync("IR-S3", "Inventory Report Theirs", LocationKind.Store);
        ProductId product = await factory.CreateProductAsync("IR-P2", "Report Soap", "7100000002");
        await InventoryControlTestSupport.SetBucketAsync(factory, mine, product, 4m, 25m);

        await factory.CreateUserAsync("ir-sm", Roles.StoreManager, locations: [mine]);

        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "ir-sm");

        using HttpResponseMessage valuation = await GetAsync(
            client, $"/api/v1/reports/inventory/valuation?locationId={mine.Value}", manager);

        valuation.StatusCode.Should().Be(HttpStatusCode.OK, await valuation.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await valuation.Content.ReadAsStringAsync());
        JsonElement row = document.RootElement.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("sku").GetString() == "IR-P2");

        row.GetProperty("quantity").GetDecimal().Should().Be(4m);
        row.GetProperty("averageUnitCost").GetDecimal().Should().Be(25m);
        row.GetProperty("totalValue").GetDecimal().Should().Be(100m);

        document.RootElement.GetProperty("totalValue").GetDecimal().Should().BeGreaterThanOrEqualTo(100m);

        using HttpResponseMessage elsewhere = await GetAsync(
            client, $"/api/v1/reports/inventory/valuation?locationId={theirs.Value}", manager);

        elsewhere.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(elsewhere)).Should().Be("report.outside_scope");
    }

    [Fact]
    public async Task MovementHistoryIsBounded()
    {
        await factory.CreateUserAsync("ir-owner", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "ir-owner");

        DateTimeOffset now = DateTimeOffset.UtcNow;

        using HttpResponseMessage forever = await GetAsync(
            client, Movements(now.AddYears(-50), now), owner);

        forever.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(forever)).Should().Be("report.period_invalid");

        using HttpResponseMessage backwards = await GetAsync(
            client, Movements(now, now.AddDays(-1)), owner);

        backwards.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using HttpResponseMessage fine = await GetAsync(client, Movements(now.AddDays(-7), now), owner);
        fine.StatusCode.Should().Be(HttpStatusCode.OK, await fine.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AgeingAndDeadStockAreOperational_AndBounded()
    {
        LocationId store = await factory.CreateLocationAsync("IR-S4", "Inventory Report Ageing", LocationKind.Store);
        ProductId product = await factory.CreateProductAsync("IR-P3", "Report Rice", "7100000003");
        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, 9m, 12m);

        // Stockroom staff, who hold report.view and not report.view.financial.
        // Neither report carries money, so both answer.
        await factory.CreateUserAsync("ir-staff2", Roles.InventoryStaff, locations: [store]);

        using HttpClient client = factory.CreateClient();
        string staff = await SignInAsync(client, "ir-staff2");

        using HttpResponseMessage ageing = await GetAsync(
            client, $"/api/v1/reports/inventory/ageing?locationId={store.Value}", staff);

        ageing.StatusCode.Should().Be(HttpStatusCode.OK, await ageing.Content.ReadAsStringAsync());

        using JsonDocument aged = JsonDocument.Parse(await ageing.Content.ReadAsStringAsync());
        JsonElement row = aged.RootElement.EnumerateArray()
            .Single(r => r.GetProperty("sku").GetString() == "IR-P3");

        row.GetProperty("onHand").GetDecimal().Should().Be(9m);

        // The product tracks no batches, so there is no received date anywhere in
        // the system and the report says so rather than calling it new.
        row.GetProperty("byAge").EnumerateArray().Single()
            .GetProperty("bucket").GetString().Should().Be("Unknown");
        row.GetProperty("oldestReceivedOn").ValueKind.Should().Be(JsonValueKind.Null);

        using HttpResponseMessage dead = await GetAsync(
            client, $"/api/v1/reports/inventory/dead-stock?locationId={store.Value}&windowDays=90", staff);

        dead.StatusCode.Should().Be(HttpStatusCode.OK, await dead.Content.ReadAsStringAsync());

        using JsonDocument deadStock = JsonDocument.Parse(await dead.Content.ReadAsStringAsync());
        JsonElement unsold = deadStock.RootElement.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("sku").GetString() == "IR-P3");

        unsold.GetProperty("lastSoldAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
        unsold.GetProperty("daysOfCover").ValueKind.Should().Be(
            JsonValueKind.Null, "nothing sold, so there is no rate to divide by");

        using HttpResponseMessage forever = await GetAsync(
            client, "/api/v1/reports/inventory/dead-stock?windowDays=100000", staff);

        forever.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(forever)).Should().Be("report.period_invalid");
    }

    private static string Movements(DateTimeOffset from, DateTimeOffset to)
    {
        // Escaped: a round-trip timestamp ends in "+00:00", and an unescaped "+"
        // in a query string is a space, which the binder rejects as malformed.
        static string Stamp(DateTimeOffset value)
            => Uri.EscapeDataString(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        return FormattableString.Invariant(
            $"/api/v1/reports/inventory/movement?from={Stamp(from)}&to={Stamp(to)}");
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

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }
}
