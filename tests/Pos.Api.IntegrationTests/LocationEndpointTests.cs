using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The location endpoints exercised through the real pipeline: authentication,
/// authorization, validation and the database.
/// </summary>
[Collection("api")]
public sealed class LocationEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task GetLocations_WithoutAuthentication_ReturnsUnauthorized()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/locations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cashier_CanListLocations()
    {
        await factory.CreateLocationAsync("MAIN", "Main Warehouse", LocationKind.MainWarehouse);
        await factory.CreateLocationAsync("STORE01", "Store One", LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        await factory.CreateUserAsync("loc-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "loc-cashier");

        using HttpResponseMessage response = await GetAsync(client, "/api/v1/locations", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] codes = document.RootElement.EnumerateArray()
            .Select(l => l.GetProperty("code").GetString()!)
            .ToArray();

        // Only active, non-system locations are listed: the supplier
        // counterparty exists in the database but must never appear here.
        codes.Should().Contain(["MAIN", "STORE01"]);
        codes.Should().NotContain(SystemLocationCodes.ExternalSupplier);
    }

    [Fact]
    public async Task Cashier_CannotCreateLocations()
    {
        await factory.CreateUserAsync("loc-cashier-create", Roles.Cashier);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "loc-cashier-create");

        using HttpResponseMessage response = await PostAsJsonAsync(
            client,
            "/api/v1/locations",
            new
            {
                code = "STORE99",
                name = "Store Ninety Nine",
                kind = (int)LocationKind.Store,
                timeZoneId = "Asia/Manila",
            },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Administrator_CanCreateAndUpdateLocationSettings()
    {
        await factory.CreateUserAsync("loc-admin", Roles.Administrator, tier: ApprovalTier.Tier3);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "loc-admin");

        using HttpResponseMessage created = await PostAsJsonAsync(
            client,
            "/api/v1/locations",
            new
            {
                code = "STORE02",
                name = "Store Two",
                kind = (int)LocationKind.Store,
                timeZoneId = "Asia/Manila",
            },
            token);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid locationId = createdDocument.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage updated = await PutAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/locations/{locationId}/settings"),
            new
            {
                negativeStockPolicy = (int)NegativeStockPolicy.AllowWithPermission,
                allowsDirectSupplierDelivery = true,
                receiptHeader = "Store Two",
            },
            token);

        updated.StatusCode.Should().Be(HttpStatusCode.NoContent);

        LocationSettings? stored = await factory.WithServiceAsync(context =>
            context.Locations.AsNoTracking()
                .Where(l => l.Id == new LocationId(locationId))
                .Select(l => l.Settings)
                .FirstAsync());

        stored.Should().NotBeNull();
        stored!.NegativeStockPolicy.Should().Be(NegativeStockPolicy.AllowWithPermission);
        stored.AllowsDirectSupplierDelivery.Should().BeTrue();
        stored.ReceiptHeader.Should().Be("Store Two");
    }

    [Fact]
    public async Task Cashier_CannotUpdateLocationSettings()
    {
        LocationId location = await factory.CreateLocationAsync("STORE03", "Store Three");
        await factory.CreateUserAsync("loc-cashier-settings", Roles.Cashier);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "loc-cashier-settings");

        using HttpResponseMessage response = await PutAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/locations/{location.Value}/settings"),
            new { receiptHeader = "tampered" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateSettings_OnSystemCounterparty_ReturnsConflict()
    {
        LocationId counterparty = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        await factory.CreateUserAsync("loc-admin-conflict", Roles.Administrator, tier: ApprovalTier.Tier3);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "loc-admin-conflict");

        using HttpResponseMessage response = await PutAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/locations/{counterparty.Value}/settings"),
            new { negativeStockPolicy = (int)NegativeStockPolicy.AllowWithPermission },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateSettings_OnUnknownLocation_ReturnsNotFound()
    {
        await factory.CreateUserAsync("loc-admin-unknown", Roles.Administrator, tier: ApprovalTier.Tier3);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "loc-admin-unknown");

        using HttpResponseMessage response = await PutAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/locations/{Guid.CreateVersion7()}/settings"),
            new { receiptHeader = "hello" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostAsJsonAsync(
        HttpClient client,
        string path,
        object body,
        string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> PutAsJsonAsync(
        HttpClient client,
        string path,
        object body,
        string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
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