using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Locations;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The cashier shift (POS.md §1): opened on a live device from a PIN session,
/// queried by everyone acting within the store, closed on the same device, and
/// refused to others — through the real pipeline.
/// </summary>
[Collection("api")]
public sealed class ShiftEndpointTests(PosApiFactory factory)
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 15);

    [Fact]
    public async Task Open_ThenSummaryThenClose_ReflectsTheLifecycle()
    {
        Seed seed = await SeedAsync("S1");

        using HttpClient client = factory.CreateClient();
        string pinToken = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);

        Guid shiftId = await OpenShiftAsync(
            client,
            pinToken,
            new
            {
                number = ShiftNumber(seed.DeviceCode01, 1),
                locationId = seed.Store.Value,
                businessDate = BusinessDate,
                openingFloat = 1000m,
            });

        using (JsonDocument openSummary = await GetSummaryAsync(client, pinToken, shiftId))
        {
            JsonElement root = openSummary.RootElement;
            root.GetProperty("number").GetString().Should().MatchRegex(
                FormattableString.Invariant($@"^SHF-\d{{4}}-{seed.DeviceCode01}-\d{{4}}$"));
            root.GetProperty("locationId").GetGuid().Should().Be(seed.Store.Value);
            root.GetProperty("deviceId").GetGuid().Should().Be(seed.Device01.Value);
            root.GetProperty("cashierUserId").GetGuid().Should().Be(seed.ManagerId.Value);
            root.GetProperty("businessDate").GetString().Should().Be(BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            root.GetProperty("openingFloat").GetDecimal().Should().Be(1000m);
            root.GetProperty("status").GetString().Should().Be("Open");
            root.GetProperty("openedAtUtc").GetDateTimeOffset().Should().BeCloseTo(
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            root.GetProperty("cashSales").GetDecimal().Should().Be(0m);
            root.GetProperty("expectedCash").GetDecimal().Should().Be(1000m);
            root.GetProperty("declaredCash").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("countedCash").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("cashVariance").ValueKind.Should().Be(JsonValueKind.Null);
        }

        using (HttpResponseMessage closed = await CloseAsync(
            client,
            pinToken,
            shiftId,
            new { locationId = seed.Store.Value, declaredCash = 1050m, countedCash = 1055m }))
        {
            closed.StatusCode.Should().Be(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());
        }

        using (JsonDocument closedSummary = await GetSummaryAsync(client, pinToken, shiftId))
        {
            JsonElement root = closedSummary.RootElement;
            root.GetProperty("status").GetString().Should().Be("Closed");
            root.GetProperty("closedAtUtc").GetDateTimeOffset().Should().BeCloseTo(
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            root.GetProperty("declaredCash").GetDecimal().Should().Be(1050m);
            root.GetProperty("countedCash").GetDecimal().Should().Be(1055m);
            root.GetProperty("cashVariance").GetDecimal().Should().Be(55m);
            root.GetProperty("expectedCash").GetDecimal().Should().Be(1000m);
        }
    }

    [Fact]
    public async Task Open_TwiceOnTheSameDevice_IsRefused()
    {
        Seed seed = await SeedAsync("S2");

        using HttpClient client = factory.CreateClient();
        string pinToken = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);

        await OpenShiftAsync(
            client,
            pinToken,
            new
            {
                number = ShiftNumber(seed.DeviceCode01, 1),
                locationId = seed.Store.Value,
                businessDate = BusinessDate,
                openingFloat = 1000m,
            });

        using HttpResponseMessage second = await PostJsonAsync(
            client,
            "/api/v1/shifts/open",
            new
            {
                number = ShiftNumber(seed.DeviceCode01, 2),
                locationId = seed.Store.Value,
                businessDate = BusinessDate,
                openingFloat = 0m,
            },
            pinToken);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict, await second.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(second)).Should().Be("shift.already_open");
    }

    [Fact]
    public async Task Open_WithMalformedNumber_ReturnsAValidationError()
    {
        Seed seed = await SeedAsync("S3");

        using HttpClient client = factory.CreateClient();
        string pinToken = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/shifts/open",
            new { number = "SHF-2026-D01-ABC", locationId = seed.Store.Value, businessDate = BusinessDate, openingFloat = 0m },
            pinToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("document.number_invalid_format");
    }

    [Fact]
    public async Task Open_AtAnExternalLocation_IsRefused()
    {
        LocationId external = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);

        await factory.CreateUserAsync(
            "s4-owner", Roles.Owner, tier: ApprovalTier.Unlimited, employeeCode: "s4own");
        DeviceId externalDevice = await factory.CreateDeviceAsync("X01", external);

        using HttpClient client = factory.CreateClient();
        string ownerToken = await PinSignInAsync(client, "s4own", externalDevice.Value);

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/shifts/open",
            new
            {
                number = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "X01", 1).Value,
                locationId = external.Value,
                businessDate = BusinessDate,
                openingFloat = 0m,
            },
            ownerToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("shift.location_external");
    }

    [Fact]
    public async Task Summary_OfAnUnknownShift_ReturnsNotFound()
    {
        Seed seed = await SeedAsync("S5");

        using HttpClient client = factory.CreateClient();
        string pinToken = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);

        using HttpResponseMessage response = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/shifts/{Guid.NewGuid()}/summary"), pinToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("shift.unknown");
    }

    [Fact]
    public async Task Summary_ByAStoreManagerOfAnotherStore_IsForbidden()
    {
        Seed storeA = await SeedAsync("S6A");
        Seed storeB = await SeedAsync("S6B");

        using HttpClient client = factory.CreateClient();
        string managerA = await PinSignInAsync(client, storeA.ManagerEmployeeCode, storeA.Device01.Value);

        Guid shiftId = await OpenShiftAsync(
            client,
            managerA,
            new
            {
                number = ShiftNumber(storeA.DeviceCode01, 1),
                locationId = storeA.Store.Value,
                businessDate = BusinessDate,
                openingFloat = 0m,
            });

        string managerB = await SignInAsync(client, storeB.ManagerUserName);

        using HttpResponseMessage outside = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/shifts/{shiftId}/summary"), managerB);

        outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, await outside.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(outside)).Should().Be("shift.outside_scope");
    }

    [Fact]
    public async Task Close_AnotherCashiersShift_WithoutThePermission_IsForbidden()
    {
        Seed seed = await SeedAsync("S7");

        await factory.CreateUserAsync(
            "s7-cashier", Roles.Cashier, locations: [seed.Store], employeeCode: "s7chs");
        DeviceId cashierDevice = await factory.CreateDeviceAsync("S7CX", seed.Store);

        using HttpClient client = factory.CreateClient();
        string managerPin = await PinSignInAsync(client, seed.ManagerEmployeeCode, seed.Device01.Value);

        Guid shiftId = await OpenShiftAsync(
            client,
            managerPin,
            new
            {
                number = ShiftNumber(seed.DeviceCode01, 1),
                locationId = seed.Store.Value,
                businessDate = BusinessDate,
                openingFloat = 1000m,
            });

        // A cashier at another till of the same store is not the shift's cashier
        // and holds no shift.close.other grant: the handler refuses them.
        string cashierPin = await PinSignInAsync(client, "s7chs", cashierDevice.Value);

        using HttpResponseMessage closed = await CloseAsync(
            client,
            cashierPin,
            shiftId,
            new { locationId = seed.Store.Value, declaredCash = 1000m, countedCash = 1000m });

        closed.StatusCode.Should().Be(HttpStatusCode.Forbidden, await closed.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(closed)).Should().Be("shift.close_other_denied");
    }

    // ---- Seeds ----------------------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix)
    {
        LocationId store = await factory.CreateLocationAsync($"SF-{suffix}", $"Shift Store {suffix}", LocationKind.Store);
        string storeManager = $"sft-{suffix}-sm";
        UserId managerId = await factory.CreateUserAsync(
            storeManager, Roles.StoreManager, locations: [store], employeeCode: $"sft{suffix}");
        string deviceCode = $"SD{suffix}";
        DeviceId device = await factory.CreateDeviceAsync(deviceCode, store);

        return new Seed(store, storeManager, managerId, $"sft{suffix}", device, deviceCode);
    }

    private static string ShiftNumber(string deviceCode, long sequence)
        => DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, deviceCode, sequence).Value;

    // ---- Request helpers ------------------------------------------------------

    private static async Task<Guid> OpenShiftAsync(HttpClient client, string accessToken, object body)
    {
        using HttpResponseMessage response = await PostJsonAsync(client, "/api/v1/shifts/open", body, accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> CloseAsync(
        HttpClient client, string accessToken, Guid shiftId, object body)
        => await PostJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/shifts/{shiftId}/close"),
            body,
            accessToken);

    private static async Task<JsonDocument> GetSummaryAsync(HttpClient client, string accessToken, Guid shiftId)
    {
        using HttpResponseMessage response = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/shifts/{shiftId}/summary"), accessToken);

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
        DeviceId Device01,
        string DeviceCode01);
}