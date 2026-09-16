using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// C18: the expired-batch sale override contract (§5) through the real API
/// pipeline — a shelf shortfall covered only by expired stock is refused with
/// <c>inventory.expired_only</c>, and the permissioned exception path completes
/// the sale, consumes the expired batch and records the override audit with the
/// cashier's reason.
/// </summary>
[Collection("api")]
public sealed class ExpiredOverrideEndpointTests(PosApiFactory factory)
{
    private static readonly DateOnly BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task Sale_WhenLineCanOnlyBeCoveredByExpiredStock_IsRefused_ExpiredOnly()
    {
        Seed seed = await SeedAsync("c18a");

        decimal sellableBefore = await BatchQuantityAsync(seed.Store, seed.Product, seed.SellableBatch);
        decimal expiredBefore = await BatchQuantityAsync(seed.Store, seed.Product, seed.ExpiredBatch);

        using HttpResponseMessage response = await PostSaleAsync(
            seed: seed, quantity: 4m, allowExpiredOverride: false);

        // The seed is signed in internally so the helper can reach the API.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("inventory.expired_only");

        // Nothing was consumed by the refused attempt.
        (await BatchQuantityAsync(seed.Store, seed.Product, seed.SellableBatch)).Should().Be(sellableBefore);
        (await BatchQuantityAsync(seed.Store, seed.Product, seed.ExpiredBatch)).Should().Be(expiredBefore);
    }

    [Fact]
    public async Task Sale_WithExpiredOverrideAndReason_CompletesFromTheExpiredBatch()
    {
        Seed seed = await SeedAsync("c18b");

        using HttpResponseMessage response = await PostSaleAsync(
            seed: seed,
            quantity: 4m,
            allowExpiredOverride: true,
            expiredOverrideReason: "Customer accepted the batch.");

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        // 4 sold: 3 from the sellable batch, 1 from the expired one.
        (await BatchQuantityAsync(seed.Store, seed.Product, seed.SellableBatch)).Should().Be(0m);
        (await BatchQuantityAsync(seed.Store, seed.Product, seed.ExpiredBatch)).Should().Be(4m);

        // The override is recorded for investigation, with the reason the
        // cashier supplied and the acting authorizer stamped from the request
        // context (the same StoreManager who signed the sale).
        AuditLogRow? audit = await factory.WithServiceAsync(context => context.AuditLog
            .AsNoTracking()
            .Where(a => a.Action == "sale.expired.override"
                && a.EntityId == seed.Product.Value
                && a.LocationId == seed.Store)
            .Select(a => new AuditLogRow(a.Reason!, a.NewValueJson!, a.UserId!.Value.Value, a.UserRoleSnapshot))
            .SingleOrDefaultAsync());
        audit.Should().NotBeNull();
        audit!.Reason.Should().Be("Customer accepted the batch.");
        audit.NewValueJson.Should().Contain("\"authorizingUserId\":");
        audit.AuthorizingUserId.Should().Be(seed.ManagerUserId.Value);
        audit.RoleSnapshot.Should().Contain("StoreManager");
    }

    [Fact]
    public async Task Sale_WithExpiredOverrideButNoReason_IsRefused()
    {
        Seed seed = await SeedAsync("c18c");

        using HttpResponseMessage response = await PostSaleAsync(
            seed: seed,
            quantity: 4m,
            allowExpiredOverride: true,
            expiredOverrideReason: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("sale.expired_override_reason_required");
    }

    [Fact]
    public async Task Sale_WithExpiredOverrideByUserWithoutPermission_IsRefused()
    {
        Seed seed = await SeedAsync("c18d");

        // A plain Cashier cannot even offer to sell from an expired batch.
        UserId cashier = await factory.CreateUserAsync(
            "c18d-cashier", Roles.Cashier, locations: [seed.Store], employeeCode: "c18dcash");

        using HttpResponseMessage response = await PostSaleAsync(
            seed: seed,
            quantity: 4m,
            allowExpiredOverride: true,
            expiredOverrideReason: "Customer accepted the batch.",
            signInUserCode: "c18dcash");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be("sale.expired_override_denied");
    }

    // ---- Seed & helpers -------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix)
    {
        string locationCode = $"C18-{suffix}";
        string locationName = $"C18 Override Store {suffix}";
        LocationId store = await factory.CreateLocationAsync(locationCode, locationName, LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalCustomer);

        string managerUserName = $"c18pt-{suffix}-sm";
        string managerEmployeeCode = $"c18{suffix}";
        UserId managerUserId = await factory.CreateUserAsync(
            managerUserName,
            Roles.StoreManager,
            locations: [store],
            employeeCode: managerEmployeeCode);

        string deviceCode = $"C18{suffix.Substring(suffix.Length - 1)}";
        DeviceId device = await factory.CreateDeviceAsync(deviceCode, store);

        // The product tracks batches and expiry: an expired batch can only
        // exist against a batch-tracked product, and the sellable shelf is fed
        // by a batch that has not yet expired.
        ProductId product = await factory.CreateProductAsync(
            $"C18-{suffix}-P1", $"Override Product {suffix}", $"6000{suffix}0001",
            tracksBatches: true, tracksExpiry: true);

        SupplierId supplier = await factory.CreateSupplierAsync($"C18-{suffix}-SUP", $"Supplier {suffix}");

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        BatchId sellable = await CreateBatchAsync(
            product, supplier, $"C18LOT{suffix}-S", today.AddDays(30), 30m);
        BatchId expired = await CreateBatchAsync(
            product, supplier, $"C18LOT{suffix}-E", today.AddDays(-15), 31m);

        UnitOfMeasureId unitId = await factory.WithServiceAsync<UnitOfMeasureId>(
            context => context.UnitsOfMeasure
                .AsNoTracking()
                .Where(u => u.Code == "PC")
                .Select(u => u.Id)
                .FirstAsync());

        await factory.CreateUserAsync($"c18pt-{suffix}-owner", Roles.Owner);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, $"c18pt-{suffix}-owner");
        using HttpResponseMessage priced = await PostJsonAsync(
            client,
            $"/api/v1/catalog/products/{product.Value}/prices",
            new { amount = 45m, reason = "C18 test price", locationId = store.Value },
            owner);
        priced.StatusCode.Should().Be(HttpStatusCode.Created, await priced.Content.ReadAsStringAsync());

        // 3 sellable units; the shelf cannot cover a request for 4.
        await SeedBalanceAsync(store, product, sellable, 3m, 30m);
        await SeedBalanceAsync(store, product, expired, 5m, 31m);

        return new Seed(store, managerUserId, managerEmployeeCode, deviceCode, device, product, unitId, sellable, expired);
    }

    private async Task<BatchId> CreateBatchAsync(
        ProductId product, SupplierId supplier, string lot, DateOnly expiresOn, decimal unitCost)
    {
        Result<Batch> created = Batch.Create(
            product,
            supplier,
            lot,
            receivedOn: DateOnly.FromDateTime(DateTime.UtcNow),
            manufacturedOn: null,
            expiresOn,
            unitCost,
            createdByUserId: new UserId(Guid.CreateVersion7()),
            now: DateTimeOffset.UtcNow);

        created.IsSuccess.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));

        await factory.WithServiceAsync(async context =>
        {
            context.Batches.Add(created.Value);
            await context.SaveChangesAsync();
            return 0;
        });

        return created.Value.Id;
    }

    private Task<int> SeedBalanceAsync(
        LocationId locationId, ProductId productId, BatchId batchId, decimal quantity, decimal unitCost)
        => factory.WithServiceAsync(async context =>
        {
            Guid batchKey = batchId.Value;
            DateTimeOffset now = DateTimeOffset.UtcNow;

            int inserted = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO inventory_balance
                     (location_id, product_id, batch_key, state, quantity,
                      average_unit_cost, total_value, last_movement_id,
                      last_movement_at_utc, version)
                 VALUES
                     ({locationId.Value}, {productId.Value}, {batchKey}, {(short)InventoryState.Available},
                      {quantity}, {unitCost}, {quantity * unitCost}, {Guid.CreateVersion7()},
                      {now}, {0})
                 """);

            inserted.Should().Be(1);
            return inserted;
        });

    private Task<decimal> BatchQuantityAsync(LocationId locationId, ProductId productId, BatchId batchId)
        => factory.WithServiceAsync(async context => (await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == locationId
                && b.ProductId == productId
                && b.BatchKey == batchId
                && b.State == InventoryState.Available)
            .SumAsync(b => (decimal?)b.Quantity)) ?? 0m);

    /// <summary>
    /// Signs the seeded manager (or an explicit employee code) in, opens a
    /// shift, and posts a sale of the seeded product at the seeded store.
    /// </summary>
    private async Task<HttpResponseMessage> PostSaleAsync(
        Seed seed,
        decimal quantity,
        bool allowExpiredOverride,
        string? expiredOverrideReason = null,
        string? signInUserCode = null)
    {
        using var http = factory.CreateClient();
        string token = await SignInManagerAsync(http, seed, signInUserCode);
        Guid shiftId = await OpenShiftAsync(http, token, seed);

        decimal total = 45m * quantity;
        return await PostJsonAsync(
            http,
            "/api/v1/sales",
            new
            {
                number = DocumentNumber.CreateForDevice(
                    DocumentType.Sale, BusinessDate.Year, seed.DeviceCode, 1).Value,
                eventId = Guid.CreateVersion7(),
                locationId = seed.Store.Value,
                cashierShiftId = shiftId,
                deviceId = seed.Device.Value,
                customerId = (Guid?)null,
                businessDate = BusinessDate,
                completedAtUtc = DateTimeOffset.UtcNow,
                lines = new[]
                {
                    new
                    {
                        productId = seed.Product.Value,
                        quantity,
                        unitOfMeasureId = seed.UnitId.Value,
                        barcode = (string?)null,
                        unitPriceOverride = (decimal?)null,
                        priceOverrideAuthorizedByUserId = (Guid?)null,
                        discount = 0m,
                        discountAuthorizedByUserId = (Guid?)null,
                        allowExpiredOverride,
                        expiredOverrideReason,
                    },
                },
                payments = new[]
                {
                    new { method = 1, amount = total, tendered = total + 20m, providerReference = (string?)null },
                },
            },
            token);
    }

    private static async Task<string> SignInManagerAsync(HttpClient client, Seed seed, string? employeeCode)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/auth/login/pin", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                employeeCode = employeeCode ?? seed.ManagerEmployeeCode,
                pin = PosApiFactory.TestPin,
            }),
        };
        request.Headers.Add("X-Device-Id", seed.Device.Value.ToString("D", CultureInfo.InvariantCulture));
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<Guid> OpenShiftAsync(HttpClient client, string accessToken, Seed seed)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/v1/shifts/open",
            new
            {
                number = DocumentNumber.CreateForDevice(
                    DocumentType.CashierShift, BusinessDate.Year, seed.DeviceCode, 1).Value,
                locationId = seed.Store.Value,
                businessDate = BusinessDate,
                openingFloat = 100m,
            },
            accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
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
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }

    private sealed record Seed(
        LocationId Store,
        UserId ManagerUserId,
        string ManagerEmployeeCode,
        string DeviceCode,
        DeviceId Device,
        ProductId Product,
        UnitOfMeasureId UnitId,
        BatchId SellableBatch,
        BatchId ExpiredBatch);

    private sealed record AuditLogRow(string Reason, string NewValueJson, Guid AuthorizingUserId, string? RoleSnapshot);
}