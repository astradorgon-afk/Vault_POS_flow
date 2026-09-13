using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Locations;
using Pos.Domain.Receipts;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Account administration through the real pipeline: creating staff, changing
/// what they may do with immediate effect, and the safeguards that stop an
/// administrator escalating anyone — themselves included — beyond their own
/// authority (ADR-0028).
/// </summary>
[Collection("api")]
public sealed class UserAdministrationEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task Administrator_CreatesACashier_WhoCanSignIn_AndSetsTheirPin()
    {
        LocationId store = await factory.CreateLocationAsync("UA-1", "Admin Store 1", LocationKind.Store);
        await factory.CreateUserAsync("ua1-admin", Roles.Administrator, tier: ApprovalTier.Tier3);
        using HttpClient client = factory.CreateClient();
        string admin = await SignInAsync(client, "ua1-admin");

        Guid cashierId;
        using (HttpResponseMessage created = await SendAsync(client, HttpMethod.Post, "/api/v1/users", admin, new
        {
            userName = "ua1-cashier",
            displayName = "Cashier Ana",
            password = PosApiFactory.TestPassword,
            employeeCode = "UA1C",
            roles = new[] { Roles.Cashier },
            locations = new[] { new { locationId = store.Value, isPrimary = true } },
        }))
        {
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            cashierId = (await ReadJsonAsync(created)).RootElement.GetProperty("id").GetGuid();
        }

        (await SignInAsync(client, "ua1-cashier")).Should().NotBeNullOrEmpty();

        using (HttpResponseMessage pin = await SendAsync(client, HttpMethod.Put, $"/api/v1/users/{cashierId}/pin", admin, new { pin = "481516" }))
        {
            pin.StatusCode.Should().Be(HttpStatusCode.NoContent, await pin.Content.ReadAsStringAsync());
        }

        using JsonDocument detail = await GetJsonAsync(client, $"/api/v1/users/{cashierId}", admin);
        JsonElement root = detail.RootElement;
        root.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal(Roles.Cashier);
        root.GetProperty("locations")[0].GetProperty("isPrimary").GetBoolean().Should().BeTrue();
        root.GetProperty("effectivePermissions").EnumerateArray().Select(p => p.GetString())
            .Should().Contain(Permissions.Sales.Create).And.NotContain(Permissions.Administration.ManageUsers);
        root.GetProperty("hasPin").GetBoolean().Should().BeTrue();

        int audited = await factory.WithServiceAsync(context => context.AuditLog
            .CountAsync(a => a.EntityId == cashierId
                             && (a.Action == AuditActions.Administration.UserCreated || a.Action == AuditActions.Authentication.PinChanged)));
        audited.Should().Be(2);
    }

    [Fact]
    public async Task RoleChange_TakesEffectOnTheNextRequest_WithoutSigningOut()
    {
        LocationId store = await factory.CreateLocationAsync("UA-2", "Admin Store 2", LocationKind.Store);
        await factory.CreateUserAsync("ua2-admin", Roles.Administrator, tier: ApprovalTier.Tier3);
        UserId managerId = await factory.CreateUserAsync("ua2-manager", Roles.StoreManager, locations: [store]);
        using HttpClient client = factory.CreateClient();
        string admin = await SignInAsync(client, "ua2-admin");
        string manager = await SignInAsync(client, "ua2-manager");

        object receipt = new { locationId = store.Value, kind = ReceiptKind.WalkInSale, amount = 10m };

        using (HttpResponseMessage before = await SendAsync(client, HttpMethod.Post, "/api/v1/receipts", manager, receipt))
        {
            before.StatusCode.Should().Be(HttpStatusCode.Created, await before.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage demoted = await SendAsync(
            client, HttpMethod.Put, $"/api/v1/users/{managerId.Value}/roles", admin, new { roles = new[] { Roles.Cashier } }))
        {
            demoted.StatusCode.Should().Be(HttpStatusCode.NoContent, await demoted.Content.ReadAsStringAsync());
        }

        // Same token, next request: the policy version moved, so the cached
        // authority is gone and receipt.create is no longer held.
        using HttpResponseMessage after = await SendAsync(client, HttpMethod.Post, "/api/v1/receipts", manager, receipt);
        after.StatusCode.Should().Be(HttpStatusCode.Forbidden, await after.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Safeguards_RefuseEscalation_SelfAdministration_AndChangingSomeoneMorePowerful()
    {
        await factory.CreateUserAsync("ua3-admin", Roles.Administrator, tier: ApprovalTier.Tier3);
        UserId adminId = await factory.CreateUserAsync("ua3-admin2", Roles.Administrator, tier: ApprovalTier.Tier3);
        UserId ownerId = await factory.CreateUserAsync("ua3-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        UserId clerkId = await factory.CreateUserAsync("ua3-clerk", Roles.InventoryStaff);
        using HttpClient client = factory.CreateClient();
        string admin = await SignInAsync(client, "ua3-admin");
        UserId selfId = await factory.WithServiceAsync(async context =>
            new UserId((await context.Users.SingleAsync(u => u.UserName == "ua3-admin")).Id));

        await ExpectAsync(HttpMethod.Put, $"/api/v1/users/{clerkId.Value}/roles", new { roles = new[] { Roles.Owner } },
            HttpStatusCode.Forbidden, "identity.privilege_escalation_forbidden");

        await ExpectAsync(HttpMethod.Post, $"/api/v1/users/{clerkId.Value}/overrides",
            new { permissionCode = Permissions.Inventory.NegativeStock, effect = PermissionEffect.Grant, reason = "Month-end count fix" },
            HttpStatusCode.Forbidden, "identity.privilege_escalation_forbidden");

        await ExpectAsync(HttpMethod.Put, $"/api/v1/users/{clerkId.Value}",
            new { displayName = "Clerk", approvalTier = ApprovalTier.Unlimited },
            HttpStatusCode.Forbidden, "identity.privilege_escalation_forbidden");

        await ExpectAsync(HttpMethod.Put, $"/api/v1/users/{selfId.Value}/roles", new { roles = new[] { Roles.Owner } },
            HttpStatusCode.Forbidden, "identity.self_administration_forbidden");

        await ExpectAsync(HttpMethod.Post, $"/api/v1/users/{selfId.Value}/disable", new { reason = "Testing self-disable" },
            HttpStatusCode.Forbidden, "identity.self_administration_forbidden");

        await ExpectAsync(HttpMethod.Post, $"/api/v1/users/{ownerId.Value}/disable", new { reason = "Attempted takeover" },
            HttpStatusCode.Forbidden, "identity.target_outranks_caller");

        // A peer with the same authority is fair game: that is ordinary administration.
        using HttpResponseMessage peer = await SendAsync(
            client, HttpMethod.Post, $"/api/v1/users/{adminId.Value}/disable", admin, new { reason = "Left the company" });
        peer.StatusCode.Should().Be(HttpStatusCode.NoContent, await peer.Content.ReadAsStringAsync());

        async Task ExpectAsync(HttpMethod method, string path, object body, HttpStatusCode status, string errorCode)
        {
            using HttpResponseMessage response = await SendAsync(client, method, path, admin, body);
            response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(response)).Should().Be(errorCode);
        }
    }

    [Fact]
    public async Task Overrides_GrantAndRemove_WithReasons_AndDisableEndsSessions()
    {
        LocationId store = await factory.CreateLocationAsync("UA-4", "Admin Store 4", LocationKind.Store);
        await factory.CreateUserAsync("ua4-admin", Roles.Administrator, tier: ApprovalTier.Tier3);
        UserId cashierId = await factory.CreateUserAsync("ua4-cashier", Roles.Cashier, locations: [store]);
        using HttpClient client = factory.CreateClient();
        string admin = await SignInAsync(client, "ua4-admin");
        string cashier = await SignInAsync(client, "ua4-cashier");

        using (HttpResponseMessage tooShort = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{cashierId.Value}/overrides", admin,
            new { permissionCode = Permissions.Receipts.View, effect = PermissionEffect.Grant, reason = "no" }))
        {
            tooShort.StatusCode.Should().Be(HttpStatusCode.BadRequest, await tooShort.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(tooShort)).Should().Be("identity.override_reason_required");
        }

        Guid overrideId;
        using (HttpResponseMessage granted = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{cashierId.Value}/overrides", admin,
            new { permissionCode = Permissions.Receipts.View, effect = PermissionEffect.Grant, reason = "Covering the manager's shift" }))
        {
            granted.StatusCode.Should().Be(HttpStatusCode.Created, await granted.Content.ReadAsStringAsync());
            overrideId = (await ReadJsonAsync(granted)).RootElement.GetProperty("id").GetGuid();
        }

        using (JsonDocument detail = await GetJsonAsync(client, $"/api/v1/users/{cashierId.Value}", admin))
        {
            detail.RootElement.GetProperty("effectivePermissions").EnumerateArray().Select(p => p.GetString())
                .Should().Contain(Permissions.Receipts.View);
            detail.RootElement.GetProperty("overrides")[0].GetProperty("isActive").GetBoolean().Should().BeTrue();
        }

        using (HttpResponseMessage removed = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/users/{cashierId.Value}/overrides/{overrideId}/remove", admin, new { reason = "Shift cover finished" }))
        {
            removed.StatusCode.Should().Be(HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync());
        }

        using (JsonDocument detail = await GetJsonAsync(client, $"/api/v1/users/{cashierId.Value}", admin))
        {
            detail.RootElement.GetProperty("effectivePermissions").EnumerateArray().Select(p => p.GetString())
                .Should().NotContain(Permissions.Receipts.View);
        }

        using (HttpResponseMessage disabled = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/users/{cashierId.Value}/disable", admin, new { reason = "Left the company" }))
        {
            disabled.StatusCode.Should().Be(HttpStatusCode.NoContent, await disabled.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage oldToken = await SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", cashier))
        {
            oldToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (HttpResponseMessage refused = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { userName = "ua4-cashier", password = PosApiFactory.TestPassword }))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ReadErrorCodeAsync(refused)).Should().Be("auth.account_disabled");
        }

        using (HttpResponseMessage enabled = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{cashierId.Value}/enable", admin))
        {
            enabled.StatusCode.Should().Be(HttpStatusCode.NoContent, await enabled.Content.ReadAsStringAsync());
        }

        (await SignInAsync(client, "ua4-cashier")).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Users_RequireUserManage_AndReportUnknownsPlainly()
    {
        LocationId store = await factory.CreateLocationAsync("UA-5", "Admin Store 5", LocationKind.Store);
        await factory.CreateUserAsync("ua5-manager", Roles.StoreManager, locations: [store]);
        await factory.CreateUserAsync("ua5-admin", Roles.Administrator, tier: ApprovalTier.Tier3);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "ua5-manager");
        string admin = await SignInAsync(client, "ua5-admin");

        using (HttpResponseMessage denied = await SendAsync(client, HttpMethod.Get, "/api/v1/users", manager))
        {
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (HttpResponseMessage missing = await SendAsync(client, HttpMethod.Get, $"/api/v1/users/{Guid.NewGuid()}", admin))
        {
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ReadErrorCodeAsync(missing)).Should().Be("identity.user_unknown");
        }

        using (HttpResponseMessage weak = await SendAsync(client, HttpMethod.Post, "/api/v1/users", admin,
            new { userName = "ua5-weak", displayName = "Weak", password = "short" }))
        {
            weak.StatusCode.Should().Be(HttpStatusCode.BadRequest, await weak.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(weak)).Should().Be("identity.password_rejected");
        }

        using (HttpResponseMessage unknownRole = await SendAsync(client, HttpMethod.Post, "/api/v1/users", admin,
            new { userName = "ua5-x", displayName = "X", password = PosApiFactory.TestPassword, roles = new[] { "Janitor" } }))
        {
            unknownRole.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorCodeAsync(unknownRole)).Should().Be("identity.role_unknown");
        }

        using JsonDocument list = await GetJsonAsync(client, "/api/v1/users?search=ua5-", admin);
        list.RootElement.EnumerateArray().Select(u => u.GetProperty("userName").GetString())
            .Should().Contain("ua5-manager").And.Contain("ua5-admin");
    }

    // ---- Helpers --------------------------------------------------------------

    internal static async Task<string> SignInAsync(HttpClient client, string userName, string? twoFactorCode = null)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword, twoFactorCode },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).RootElement.GetProperty("accessToken").GetString()!;
    }

    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string accessToken, object? body = null)
    {
        using HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, CancellationToken.None);
    }

    internal static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path, string accessToken)
    {
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, path, accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadJsonAsync(response);
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    internal static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(content);
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }
}
