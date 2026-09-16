using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Identity;
using Pos.Domain.Locations;
using Pos.Infrastructure.Identity;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The web-terminal checkout orchestration (docs/POS.md §2): browser registers
/// listed for a store, the session context a checkout needs, and the server-side
/// allocation of a register's device-scoped numbers — through the real pipeline.
/// </summary>
[Collection("api")]
public sealed class TerminalEndpointTests(PosApiFactory factory)
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 15);

    [Fact]
    public async Task Registers_ListsOnlyActiveWebRegistersAtTheStore()
    {
        Seed seed = await SeedAsync("TR1");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/registers?locationId={seed.Store.Value}"),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement registers = document.RootElement;
        registers.GetArrayLength().Should().Be(2);
        registers[0].GetProperty("shortCode").GetString().Should().Be(seed.WebCodeA);
        registers[0].GetProperty("name").GetString().Should().Be("Web " + seed.WebCodeA);
        registers[1].GetProperty("shortCode").GetString().Should().Be(seed.WebCodeB);
        registers[1].GetProperty("id").GetGuid().Should().Be(seed.WebB.Value);
    }

    [Fact]
    public async Task Registers_WantsSaleCreateAtTheStore()
    {
        Seed seed = await SeedAsync("TR2");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.AuditorUserName);

        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/registers?locationId={seed.Store.Value}"),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Session_WithoutRegisterHeader_IsRefused()
    {
        Seed seed = await SeedAsync("TR3");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/session?locationId={seed.Store.Value}"),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("device.required");
    }

    [Fact]
    public async Task Session_ReturnsBusinessDateSettingsAndTheOpenShift()
    {
        Seed seed = await SeedAsync("TR4");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        // The cashier starts their shift: the web allocates the SHF number on
        // the server, then opens the shift through the existing endpoint.
        string shiftNumber = await NextNumberAsync(client, token, seed.Store.Value, seed.WebA.Value, "SHF");

        using HttpResponseMessage opened = await PostJsonAsync(
            client,
            "/api/v1/shifts/open",
            new
            {
                number = shiftNumber,
                locationId = seed.Store.Value,
                businessDate = BusinessDate,
                openingFloat = 1000m,
            },
            token,
            seed.WebA.Value);

        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());

        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/session?locationId={seed.Store.Value}"),
            token,
            seed.WebA.Value);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        root.GetProperty("deviceId").GetGuid().Should().Be(seed.WebA.Value);
        root.GetProperty("deviceShortCode").GetString().Should().Be(seed.WebCodeA);
        root.GetProperty("deviceName").GetString().Should().Be("Web " + seed.WebCodeA);
        root.GetProperty("businessDate").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("vatRate").GetDecimal().Should().Be(0.12m);
        root.GetProperty("cashRoundingIncrement").GetDecimal().Should().Be(0.01m);

        JsonElement open = root.GetProperty("openShift");
        open.ValueKind.Should().Be(JsonValueKind.Object);
        open.GetProperty("number").GetString().Should().Be(shiftNumber);
        open.GetProperty("cashierId").GetGuid().Should().Be(seed.CashierId.Value);
        open.GetProperty("cashierName").GetString().Should().Be(seed.CashierUserName);
        open.GetProperty("businessDate").GetString().Should().Be(BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        open.GetProperty("openingFloat").GetDecimal().Should().Be(1000m);

        Guid shiftId = open.GetProperty("shiftId").GetGuid();
        shiftId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Session_WithoutAnOpenShift_ReportsNone()
    {
        Seed seed = await SeedAsync("TR5");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/session?locationId={seed.Store.Value}"),
            token,
            seed.WebA.Value);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("openShift").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Session_RefusesARegisterBelongingElsewhere()
    {
        Seed seed = await SeedAsync("TR6");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/session?locationId={seed.OtherStore.Value}"),
            token,
            seed.WebA.Value);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("device.wrong_location");
    }

    [Fact]
    public async Task Session_RefusesASuspendedRegister()
    {
        Seed seed = await SeedAsync("TR7");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/session?locationId={seed.Store.Value}"),
            token,
            seed.WebSuspended.Value);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("device.not_active");
    }

    [Fact]
    public async Task NextNumber_AllocatesSequentialNumbersPerDocumentType()
    {
        Seed seed = await SeedAsync("TR8");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        string sal1 = await NextNumberAsync(client, token, seed.Store.Value, seed.WebA.Value, "SAL");
        string sal2 = await NextNumberAsync(client, token, seed.Store.Value, seed.WebA.Value, "SAL");
        string ret1 = await NextNumberAsync(client, token, seed.Store.Value, seed.WebA.Value, "RET");
        string shf1 = await NextNumberAsync(client, token, seed.Store.Value, seed.WebA.Value, "SHF");

        sal1.Should().MatchRegex(FormattableString.Invariant(
            $@"^SAL-\d{{4}}-{seed.WebCodeA}-000001$"));
        sal2.Should().MatchRegex(FormattableString.Invariant(
            $@"^SAL-\d{{4}}-{seed.WebCodeA}-000002$"));
        ret1.Should().MatchRegex(FormattableString.Invariant(
            $@"^RET-\d{{4}}-{seed.WebCodeA}-000001$"));
        shf1.Should().MatchRegex(FormattableString.Invariant(
            $@"^SHF-\d{{4}}-{seed.WebCodeA}-0001$"));
    }

    [Fact]
    public async Task NextNumber_ShiftNumberNeedsTheShiftOpenPermission()
    {
        Seed seed = await SeedAsync("TR9");

        // A cashier who can sell but, by explicit override, may not open shifts
        // still allocates sale numbers — never shift numbers.
        await DenyAsync(seed.CashierUserName, Permissions.Sales.OpenShift);

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        using (HttpResponseMessage allocation = await PostJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/{seed.Store.Value}/next-number"),
            new { documentType = "SAL" },
            token,
            seed.WebA.Value))
        {
            allocation.StatusCode.Should().Be(HttpStatusCode.OK, await allocation.Content.ReadAsStringAsync());
        }

        using HttpResponseMessage refused = await PostJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/{seed.Store.Value}/next-number"),
            new { documentType = "SHF" },
            token,
            seed.WebA.Value);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(refused)).Should().Be("authorization.denied");
    }

    [Fact]
    public async Task NextNumber_RejectsUnknownDocumentTypes()
    {
        Seed seed = await SeedAsync("TR10");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/{seed.Store.Value}/next-number"),
            new { documentType = "GRN" },
            token,
            seed.WebA.Value);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("terminal.document_type_invalid");
    }

    [Fact]
    public async Task NextNumber_RejectsAPhysicalRegistersDocuments()
    {
        Seed seed = await SeedAsync("TR11");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/{seed.Store.Value}/next-number"),
            new { documentType = "SAL" },
            token,
            seed.WindowsDevice.Value);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("device.not_web");
    }

    [Fact]
    public async Task NextNumber_RejectsASuspendedRegister()
    {
        Seed seed = await SeedAsync("TR12");

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed.CashierUserName);

        using HttpResponseMessage response = await PostJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/{seed.Store.Value}/next-number"),
            new { documentType = "SAL" },
            token,
            seed.WebSuspended.Value);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("device.not_active");
    }

    // ---- Seeds ----------------------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix)
    {
        LocationId store = await factory.CreateLocationAsync($"TF-{suffix}", $"Terminal Store {suffix}", LocationKind.Store);
        LocationId otherStore = await factory.CreateLocationAsync($"TF-{suffix}-B", $"Terminal Store B {suffix}", LocationKind.Store);

        // Device short codes are 2-6 characters, so only the numeric part of
        // the suffix goes into them ("TR10" -> "TW10A", not "TWTR10A").
        string num = suffix[2..];

        string cashier = $"tfn-{suffix}-cs";
        UserId cashierId = await factory.CreateUserAsync(cashier, Roles.Cashier, locations: [store, otherStore]);

        string auditor = $"tfn-{suffix}-au";
        await factory.CreateUserAsync(auditor, Roles.Auditor, locations: [store]);

        DeviceId webA = await factory.CreateWebDeviceAsync($"TW{num}A", store);
        DeviceId webB = await factory.CreateWebDeviceAsync($"TW{num}B", store);
        DeviceId webSuspended = await factory.CreateWebDeviceAsync($"TW{num}Z", store, DeviceStatus.Suspended);
        DeviceId windows = await factory.CreateDeviceAsync($"WD{num}", store);

        return new Seed(
            store,
            otherStore,
            cashier,
            cashierId,
            auditor,
            $"TW{num}A",
            $"TW{num}B",
            webA,
            webB,
            webSuspended,
            windows);
    }

    private Task<bool> DenyAsync(string userName, string permissionCode)
        => factory.WithServiceAsync(async context =>
        {
            AppUser? user = await context.Users.SingleAsync(u => u.UserName == userName);
            UserId userId = new(user.Id);

            UserPermissionOverride? existing = await context.UserPermissionOverrides
                .FirstOrDefaultAsync(o => o.UserId == userId && o.PermissionCode == permissionCode);

            if (existing is null)
            {
                context.UserPermissionOverrides.Add(UserPermissionOverride.Create(
                    userId,
                    permissionCode,
                    PermissionEffect.Deny,
                    DateTimeOffset.UtcNow,
                    userId,
                    "Denied for the terminal shift-number gate test.").Value);
                await context.SaveChangesAsync();

                // The database-backed evaluator caches authorization per policy
                // version, so the deny must bump it before it takes effect.
                await factory.WithServiceAsync<IPolicyVersionProvider>(async policyVersion =>
                    await policyVersion.BumpAsync("test deny override", CancellationToken.None));
            }

            return true;
        });

    // ---- Request helpers ------------------------------------------------------

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

    private static async Task<string> NextNumberAsync(
        HttpClient client, string token, Guid locationId, Guid deviceId, string documentType)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/terminal/{locationId}/next-number"),
            new { documentType },
            token,
            deviceId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("documentType").GetString().Should().Be(documentType);
        return document.RootElement.GetProperty("number").GetString()!;
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string path, string token, Guid? deviceId = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (deviceId is { } device)
        {
            request.Headers.Add("X-Device-Id", device.ToString("D", CultureInfo.InvariantCulture));
        }

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client, string path, object body, string token, Guid deviceId)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Device-Id", deviceId.ToString("D", CultureInfo.InvariantCulture));

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
        LocationId OtherStore,
        string CashierUserName,
        UserId CashierId,
        string AuditorUserName,
        string WebCodeA,
        string WebCodeB,
        DeviceId WebA,
        DeviceId WebB,
        DeviceId WebSuspended,
        DeviceId WindowsDevice);
}