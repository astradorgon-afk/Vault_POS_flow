using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Auditing;

namespace Pos.Api.IntegrationTests;

/// <summary>Customer lookup and account lifecycle through the authenticated HTTP pipeline.</summary>
[Collection("api")]
public sealed class CustomerEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task CustomerLifecycle_SearchesAndAuditsWithoutDuplicatingPii()
    {
        await factory.CreateUserAsync("customer-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, "customer-cashier");

        using HttpResponseMessage created = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/customers", new
        {
            displayName = "  Maria Santos  ",
            phone = "09171234567",
            email = "maria@example.test",
            tin = "123-456-789",
            note = "Prefers SMS",
        }, token);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        Guid id = await ReadIdAsync(created);

        using HttpResponseMessage searched = await GetAsync(client, "/api/v1/customers?search=MARIA&page=1&pageSize=10", token);
        searched.StatusCode.Should().Be(HttpStatusCode.OK);
        using (JsonDocument json = JsonDocument.Parse(await searched.Content.ReadAsStringAsync()))
        {
            json.RootElement.GetProperty("total").GetInt32().Should().Be(1);
            JsonElement customer = json.RootElement.GetProperty("customers")[0];
            customer.GetProperty("id").GetGuid().Should().Be(id);
            customer.GetProperty("displayName").GetString().Should().Be("Maria Santos");
        }

        using HttpResponseMessage updated = await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/customers/{id}", new
        {
            displayName = "Maria S. Santos",
            phone = "09171234567",
            email = "maria.santos@example.test",
            tin = "123-456-789",
            note = "Updated contact",
        }, token);
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());

        using HttpResponseMessage deactivated = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/customers/{id}/deactivate", new { reason = "Duplicate profile" }, token);
        deactivated.StatusCode.Should().Be(HttpStatusCode.OK, await deactivated.Content.ReadAsStringAsync());

        using HttpResponseMessage blockedUpdate = await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/customers/{id}", new
        {
            displayName = "Blocked change",
            phone = (string?)null,
            email = (string?)null,
            tin = (string?)null,
            note = (string?)null,
        }, token);
        blockedUpdate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(blockedUpdate)).Should().Be("customer.inactive");

        using HttpResponseMessage reactivated = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/customers/{id}/reactivate", new { }, token);
        reactivated.StatusCode.Should().Be(HttpStatusCode.OK, await reactivated.Content.ReadAsStringAsync());

        using HttpResponseMessage detail = await GetAsync(client, $"/api/v1/customers/{id}", token);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        using (JsonDocument json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
        {
            json.RootElement.GetProperty("isActive").GetBoolean().Should().BeTrue();
            json.RootElement.GetProperty("displayName").GetString().Should().Be("Maria S. Santos");
            json.RootElement.GetProperty("deactivationReason").ValueKind.Should().Be(JsonValueKind.Null);
        }

        await factory.WithServiceAsync(async context =>
        {
            List<AuditLogEntry> entries = await context.AuditLog.AsNoTracking()
                .Where(entry => entry.EntityId == id && entry.EntityType == "Customer")
                .OrderBy(entry => entry.OccurredAtUtc)
                .ToListAsync();
            entries.Select(entry => entry.Action).Should().BeEquivalentTo(
                AuditActions.Sales.CustomerCreated,
                AuditActions.Sales.CustomerUpdated,
                AuditActions.Sales.CustomerDeactivated,
                AuditActions.Sales.CustomerReactivated);
            entries.Should().OnlyContain(entry => entry.PreviousValueJson == null && entry.NewValueJson == null);
            entries.Single(entry => entry.Action == AuditActions.Sales.CustomerDeactivated)
                .Reason.Should().Be("Duplicate profile");
            return true;
        });
    }

    [Fact]
    public async Task CustomerPermissions_SeparateReadFromManagement()
    {
        await factory.CreateUserAsync("customer-manager", Roles.StoreManager);
        await factory.CreateUserAsync("customer-auditor", Roles.Auditor);
        await factory.CreateUserAsync("customer-inventory", Roles.InventoryStaff);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "customer-manager");
        string auditor = await SignInAsync(client, "customer-auditor");
        string inventory = await SignInAsync(client, "customer-inventory");

        using HttpResponseMessage created = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/customers",
            new { displayName = "Permission Customer", phone = (string?)null, email = (string?)null,
                tin = (string?)null, note = (string?)null }, manager);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        Guid id = await ReadIdAsync(created);

        using HttpResponseMessage auditRead = await GetAsync(client, $"/api/v1/customers/{id}", auditor);
        auditRead.StatusCode.Should().Be(HttpStatusCode.OK);
        using HttpResponseMessage auditWrite = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/customers/{id}/deactivate", new { reason = "Not allowed" }, auditor);
        auditWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage inventoryRead = await GetAsync(client, "/api/v1/customers", inventory);
        inventoryRead.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage anonymousRead = await client.GetAsync($"/api/v1/customers/{id}");
        anonymousRead.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidAndUnknownCustomerRequests_ReturnStableProblems()
    {
        await factory.CreateUserAsync("customer-validation", Roles.Cashier);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, "customer-validation");

        using HttpResponseMessage invalid = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/customers",
            new { displayName = "Customer", phone = (string?)null, email = "bad-email",
                tin = (string?)null, note = (string?)null }, token);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using HttpResponseMessage unknown = await GetAsync(client, $"/api/v1/customers/{Guid.CreateVersion7()}", token);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorCodeAsync(unknown)).Should().Be("customer.unknown");
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { userName, password = PosApiFactory.TestPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client, HttpMethod method, string path, object body, string token)
    {
        using HttpRequestMessage request = new(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }
}
