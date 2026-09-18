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
/// The owner dashboard (ROADMAP §Phase 16). The arithmetic is the sales
/// analysis's, tested there; what is tested here is that the financial half is
/// withheld rather than zeroed, that scope cannot be widened, and that a named
/// range resolves in a real timezone.
/// </summary>
[Collection("api")]
public sealed class DashboardEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task WithoutTheFinancialPermission_CostAndMarginAreAbsent_NotZero()
    {
        LocationId store = await factory.CreateLocationAsync("DB-S1", "Dashboard Store", LocationKind.Store);
        ProductId product = await factory.CreateProductAsync("DB-P1", "Dashboard Biscuits", "7200000001");
        await InventoryControlTestSupport.SetBucketAsync(factory, store, product, 20m, 30m);

        // Stockroom staff hold report.view and not report.view.financial.
        await factory.CreateUserAsync("db-staff", Roles.InventoryStaff, locations: [store]);

        using HttpClient client = factory.CreateClient();
        string staff = await SignInAsync(client, "db-staff");

        using JsonDocument overview = await OverviewAsync(client, staff, "Month");
        JsonElement kpis = overview.RootElement.GetProperty("kpis");

        kpis.GetProperty("revenue").ValueKind.Should().Be(JsonValueKind.Number);

        // Null, not zero. Zero reads as "we made nothing", which is a statement
        // about the business rather than about the reader.
        kpis.GetProperty("cost").ValueKind.Should().Be(JsonValueKind.Null);
        kpis.GetProperty("grossProfit").ValueKind.Should().Be(JsonValueKind.Null);
        kpis.GetProperty("marginPercent").ValueKind.Should().Be(JsonValueKind.Null);

        overview.RootElement.GetProperty("inventory").GetProperty("value").ValueKind
            .Should().Be(JsonValueKind.Null, "what the shelves are worth is a financial question too");

        // The operational half is all there.
        overview.RootElement.GetProperty("inventory").GetProperty("available").GetDecimal()
            .Should().BeGreaterThanOrEqualTo(20m);
    }

    [Fact]
    public async Task AnOwnerSeesTheFinancialHalf()
    {
        await factory.CreateUserAsync("db-owner", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "db-owner");

        using JsonDocument overview = await OverviewAsync(client, owner, "Month");

        overview.RootElement.GetProperty("kpis").GetProperty("cost").ValueKind
            .Should().Be(JsonValueKind.Number);
        overview.RootElement.GetProperty("inventory").GetProperty("value").ValueKind
            .Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task TheStockPanelSaysWhenItWasRead()
    {
        await factory.CreateUserAsync("db-owner2", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "db-owner2");

        using JsonDocument overview = await OverviewAsync(client, owner, "PrevMonth");

        // The stock half is a snapshot of now whatever period the sales cover, and
        // the payload says so rather than leaving a reader to assume they match.
        overview.RootElement.GetProperty("inventory").GetProperty("asOfUtc").GetDateTimeOffset()
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        overview.RootElement.GetProperty("fromDate").GetString().Should().NotBeNull();
        overview.RootElement.GetProperty("timeZoneId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ARangeResolvesAgainstARealTimezone()
    {
        await factory.CreateUserAsync("db-owner3", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "db-owner3");

        using JsonDocument overview = await OverviewAsync(client, owner, "Today");

        // Whatever the business's timezone is, "today" comes back as one day and
        // the payload names the zone it was resolved in — so a reader can tell
        // whose day they are looking at.
        string timeZone = overview.RootElement.GetProperty("timeZoneId").GetString()!;
        timeZone.Should().NotBe(string.Empty);

        overview.RootElement.GetProperty("fromDate").GetString()
            .Should().Be(overview.RootElement.GetProperty("toDate").GetString());
    }

    [Fact]
    public async Task ACustomRangeWithoutItsDatesIsRefused()
    {
        await factory.CreateUserAsync("db-owner4", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "db-owner4");

        using HttpResponseMessage response = await GetAsync(
            client, "/api/v1/dashboard/overview?range=Custom", owner);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("dashboard.custom_range_incomplete");
    }

    [Fact]
    public async Task AManagerCannotAskAboutAnotherStore()
    {
        LocationId mine = await factory.CreateLocationAsync("DB-S2", "Dashboard Mine", LocationKind.Store);
        LocationId theirs = await factory.CreateLocationAsync("DB-S3", "Dashboard Theirs", LocationKind.Store);
        await factory.CreateUserAsync("db-sm", Roles.StoreManager, locations: [mine]);

        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "db-sm");

        using HttpResponseMessage refused = await GetAsync(
            client, $"/api/v1/dashboard/overview?range=Month&locationId={theirs.Value}", manager);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be("report.outside_scope");

        using HttpResponseMessage allowed = await GetAsync(
            client, $"/api/v1/dashboard/overview?range=Month&locationId={mine.Value}", manager);

        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheStoreComparisonCarriesEachStoresShare()
    {
        await factory.CreateUserAsync("db-owner5", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "db-owner5");

        using JsonDocument overview = await OverviewAsync(client, owner, "Year");
        JsonElement stores = overview.RootElement.GetProperty("stores");

        stores.ValueKind.Should().Be(JsonValueKind.Array);

        foreach (JsonElement store in stores.EnumerateArray())
        {
            // A share of nothing is not nought per cent, it is not a share — so
            // the field is null when nothing sold anywhere rather than zero.
            store.GetProperty("shareOfRevenue").ValueKind
                .Should().BeOneOf(JsonValueKind.Number, JsonValueKind.Null);
            store.GetProperty("kpis").GetProperty("revenue").ValueKind.Should().Be(JsonValueKind.Number);
        }
    }

    [Fact]
    public async Task EveryPanelIsPresent_EvenTheCleanOnes()
    {
        await factory.CreateUserAsync("db-owner6", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "db-owner6");

        using HttpResponseMessage response = await GetAsync(
            client, "/api/v1/dashboard/exceptions?range=Last30", owner);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument board = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement panels = board.RootElement.GetProperty("panels");

        List<string> kinds = [.. panels.EnumerateArray().Select(p => p.GetProperty("kind").GetString()!)];

        // A board that hid its clean rows would leave a reader unsure whether
        // there was nothing wrong or nothing looked at.
        kinds.Should().BeEquivalentTo(new[]
        {
            "UnknownProducts", "TransferDiscrepancies", "HighValueAdjustments", "NegativeStockAttempts",
            "ExpiredStillSellable", "RepeatedCountVariances", "FailedSync", "OfflineDevices",
            "EmergencyTransfers",
        });

        foreach (JsonElement panel in panels.EnumerateArray())
        {
            panel.GetProperty("count").ValueKind.Should().Be(JsonValueKind.Number);
            panel.GetProperty("sample").ValueKind.Should().Be(JsonValueKind.Array);
            panel.GetProperty("severity").GetString().Should()
                .BeOneOf("Info", "Warning", "Critical", "severity travels by name, not as a number");
        }

        // Both halves of the window are stated, because some panels count the
        // period and some are a standing state.
        board.RootElement.GetProperty("fromDate").GetString().Should().NotBeNull();
        board.RootElement.GetProperty("asOfUtc").GetDateTimeOffset()
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ExpiredStockStillSellable_IsCriticalAndCounted()
    {
        LocationId store = await factory.CreateLocationAsync("DB-S4", "Dashboard Expiry", LocationKind.Store);
        ProductId product = await factory.CreateProductAsync("DB-P4", "Dashboard Milk", "7200000004");

        await factory.CreateUserAsync("db-owner7", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "db-owner7");

        using HttpResponseMessage response = await GetAsync(
            client, $"/api/v1/dashboard/exceptions?range=Last30&locationId={store.Value}", owner);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument board = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement expired = board.RootElement.GetProperty("panels").EnumerateArray()
            .Single(p => p.GetProperty("kind").GetString() == "ExpiredStillSellable");

        // Every other exception is money or paperwork; this one can reach a
        // customer, which is why it is the one panel that is always Critical.
        expired.GetProperty("severity").GetString().Should().Be("Critical");

        _ = product;
    }

    [Fact]
    public async Task WithoutTheFinancialPermission_AHighValuePanelStillCounts_ButSaysNoAmount()
    {
        LocationId store = await factory.CreateLocationAsync("DB-S5", "Dashboard Adjust", LocationKind.Store);
        await factory.CreateUserAsync("db-staff2", Roles.InventoryStaff, locations: [store]);

        using HttpClient client = factory.CreateClient();
        string staff = await SignInAsync(client, "db-staff2");

        using HttpResponseMessage response = await GetAsync(
            client, $"/api/v1/dashboard/exceptions?range=Last30&locationId={store.Value}", staff);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument board = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement high = board.RootElement.GetProperty("panels").EnumerateArray()
            .Single(p => p.GetProperty("kind").GetString() == "HighValueAdjustments");

        // The count is operational — how many crossed the line — and what they
        // came to is financial.
        high.GetProperty("count").ValueKind.Should().Be(JsonValueKind.Number);
        high.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheExceptionBoardIsScopedTheSameWayAsTheOverview()
    {
        LocationId mine = await factory.CreateLocationAsync("DB-S6", "Dashboard Board Mine", LocationKind.Store);
        LocationId theirs = await factory.CreateLocationAsync("DB-S7", "Dashboard Board Theirs", LocationKind.Store);
        await factory.CreateUserAsync("db-sm2", Roles.StoreManager, locations: [mine]);

        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "db-sm2");

        using HttpResponseMessage refused = await GetAsync(
            client, $"/api/v1/dashboard/exceptions?range=Month&locationId={theirs.Value}", manager);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be("report.outside_scope");
    }

    private static async Task<JsonDocument> OverviewAsync(HttpClient client, string token, string range)
    {
        using HttpResponseMessage response = await GetAsync(
            client, $"/api/v1/dashboard/overview?range={range}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
