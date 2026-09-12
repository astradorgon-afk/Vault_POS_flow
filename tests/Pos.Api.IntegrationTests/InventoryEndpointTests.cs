using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Pos.Application.Identity;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The inventory reconciliation endpoints exercised through the real pipeline:
/// authentication, authorization, the maintenance gate, and the reconciler itself.
/// </summary>
[Collection("api")]
public sealed class InventoryEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task Reconcile_OnAnEmptyLedger_ReportsHealthy()
    {
        await factory.CreateUserAsync("inv-admin-reconcile", Roles.Administrator);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "inv-admin-reconcile");

        using HttpResponseMessage response = await PostAsync(client, "/api/v1/inventory/reconcile", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isHealthy").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("bucketCount").GetInt32().Should().Be(0);
        document.RootElement.GetProperty("wasRebuilt").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("discrepancies").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_IsRefused_ForARoleWithoutTheMaintenancePermission()
    {
        await factory.CreateUserAsync("inv-cashier-reconcile", Roles.Cashier);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "inv-cashier-reconcile");

        using HttpResponseMessage response = await PostAsync(client, "/api/v1/inventory/reconcile", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RebuildBalances_IsWithheld_WhileTheMaintenanceSwitchIsOff()
    {
        await factory.CreateUserAsync("inv-admin-rebuild-off", Roles.Administrator);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "inv-admin-rebuild-off");

        using HttpResponseMessage response = await PostAsync(client, "/api/v1/inventory/rebuild-balances", token);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("errorCode").GetString().Should().Be("maintenance.disabled");
    }

    [Fact]
    public async Task RebuildBalances_WithTheSwitchOn_RunsAgainstTheLedger()
    {
        // The maintenance switch is configuration read once at host start, so the
        // switch-on case runs its own host on its own database. That is done with
        // an independent factory instance, never WithWebHostBuilder on the
        // collection fixture: disposing a derived factory tears down shared host
        // state (the signing key) and breaks every later test on the parent.
        await using PosApiFactory rebuildFactory = new(new Dictionary<string, string?>
        {
            ["Maintenance__AllowBalanceRebuild"] = "true",
        });
        await rebuildFactory.InitializeAsync();

        await rebuildFactory.CreateUserAsync("inv-admin-rebuild-on", Roles.Administrator);

        using HttpClient client = rebuildFactory.CreateClient();
        string token = await SignInAsync(client, "inv-admin-rebuild-on");

        using HttpResponseMessage response = await PostAsync(client, "/api/v1/inventory/rebuild-balances", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isHealthy").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("bucketCount").GetInt32().Should().Be(0);
        document.RootElement.GetProperty("wasRebuilt").GetBoolean().Should().BeFalse(
            because: "with nothing to repair the reconciler writes nothing");
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(request, CancellationToken.None);
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
}