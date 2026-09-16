using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using static Pos.Api.IntegrationTests.ProductCurationEndpointTests;
using static Pos.Api.IntegrationTests.UserAdministrationEndpointTests;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Effective-dated prices, per-location stocking settings, unit conversions and
/// supplier links through the real pipeline (ADR-0029 for pricing).
/// </summary>
[Collection("api")]
public sealed class ProductStockingEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task Prices_SupersedeAndResume_AndOnlyPriceManagersMayChangeThem()
    {
        ProductId product = await factory.CreateProductAsync("CUR-PRICE-01", "Canned Tuna", "4800000300014");
        LocationId store = await factory.CreateLocationAsync("CUR-PRICE-S", "Pricing Store");
        await factory.CreateUserAsync("cur-price-owner", Roles.Owner);
        await factory.CreateUserAsync("cur-price-mgr", Roles.MainInventoryManager);
        using HttpClient client = factory.CreateClient();

        string owner = await SignInAsync(client, "cur-price-owner");
        string manager = await SignInAsync(client, "cur-price-mgr");
        string prices = Path(product, "/prices");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await ExpectAsync(client, prices, manager, new { amount = 50m, reason = "Launch price" }, HttpStatusCode.Forbidden);
        await ExpectAsync(client, prices, owner, new { amount = 50m, reason = "Launch price" }, HttpStatusCode.Created);
        await ExpectAsync(
            client, prices, owner,
            new { amount = 40m, reason = "Weekend promo", effectiveFromUtc = now.AddDays(1), effectiveToUtc = now.AddDays(2) },
            HttpStatusCode.Created);

        using (JsonDocument listed = await GetJsonAsync(client, prices, manager))
        {
            JsonElement[] rows = [.. listed.RootElement.EnumerateArray()];
            rows.Select(r => r.GetProperty("amount").GetDecimal()).Should().Equal(50m, 40m, 50m);
            rows.Should().ContainSingle(r => r.GetProperty("isCurrent").GetBoolean())
                .Which.GetProperty("amount").GetDecimal().Should().Be(50m);
        }

        await ExpectAsync(
            client, prices, owner, new { amount = 45m, reason = "Backdated", effectiveFromUtc = now.AddDays(-1) },
            HttpStatusCode.BadRequest, "catalog.price_backdated");
        await ExpectAsync(
            client, prices, owner, new { amount = 45m, reason = "Would cancel the promo", effectiveFromUtc = now.AddHours(36) },
            HttpStatusCode.Conflict, "catalog.price_overlap");
        await ExpectAsync(client, prices, owner, new { amount = 45m }, HttpStatusCode.BadRequest, "catalog.reason_required");
        await ExpectAsync(
            client, prices, owner, new { amount = 45m, reason = "Nowhere", locationId = Guid.CreateVersion7() },
            HttpStatusCode.Conflict, "catalog.location_unknown");
        await ExpectAsync(
            client, prices, owner, new { amount = 48m, reason = "Store price match", locationId = store.Value },
            HttpStatusCode.Created);

        using (JsonDocument storePrices = await GetJsonAsync(client, prices + "?locationId=" + store.Value, manager))
        {
            storePrices.RootElement.EnumerateArray().Should().ContainSingle()
                .Which.GetProperty("amount").GetDecimal().Should().Be(48m);
        }

        int audited = await factory.WithServiceAsync(context => context.AuditLog
            .CountAsync(a => a.EntityId == product.Value && a.Action == AuditActions.Catalog.PriceChanged));
        audited.Should().Be(3);
    }

    [Fact]
    public async Task ScheduledPriceCanBeCancelledBeforeItTakesEffect_AndOnlyPriceManagersMayCancel()
    {
        ProductId product = await factory.CreateProductAsync("CUR-CANCEL-01", "Canned Corn", "4800000300045");
        await factory.CreateUserAsync("cur-cancel-owner", Roles.Owner);
        await factory.CreateUserAsync("cur-cancel-mgr", Roles.MainInventoryManager);
        using HttpClient client = factory.CreateClient();

        string owner = await SignInAsync(client, "cur-cancel-owner");
        string manager = await SignInAsync(client, "cur-cancel-mgr");
        string prices = Path(product, "/prices");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using (HttpResponseMessage baseCreated = await SendAsync(
                   client, HttpMethod.Post, prices, owner, new { amount = 50m, reason = "Launch price" }))
        {
            baseCreated.StatusCode.Should().Be(HttpStatusCode.Created);
            Guid basePriceId = (await ReadJsonAsync(baseCreated)).RootElement.GetProperty("id").GetGuid();

            await ExpectAsync(
                client, $"{prices}/{basePriceId}/cancel", manager, new { reason = "Not allowed" },
                HttpStatusCode.Forbidden);
        }

        Guid promoId;
        using (HttpResponseMessage promoCreated = await SendAsync(
                   client, HttpMethod.Post, prices, owner,
                   new { amount = 40m, reason = "Weekend promo", effectiveFromUtc = now.AddDays(1), effectiveToUtc = now.AddDays(2) }))
        {
            promoCreated.StatusCode.Should().Be(HttpStatusCode.Created);
            promoId = (await ReadJsonAsync(promoCreated)).RootElement.GetProperty("id").GetGuid();
        }

        using (HttpResponseMessage cancelled = await SendAsync(
                   client, HttpMethod.Post, $"{prices}/{promoId}/cancel", owner, new { reason = "Promo cancelled by buyer" }))
        {
            cancelled.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReadJsonAsync(cancelled)).RootElement.GetProperty("id").GetGuid().Should().Be(promoId);
        }

        using (JsonDocument listed = await GetJsonAsync(client, prices, manager))
        {
            JsonElement[] rows = [.. listed.RootElement.EnumerateArray()];
            rows.Should().ContainSingle(r => r.GetProperty("isCurrent").GetBoolean())
                .Which.GetProperty("amount").GetDecimal().Should().Be(50m, "the base price carries through the cancelled period");
        }

        await ExpectAsync(
            client, $"{prices}/{promoId}/cancel", owner, new { reason = "Already gone" },
            HttpStatusCode.NotFound, "catalog.price_unknown");
        await ExpectAsync(
            client, $"{prices}/{Guid.CreateVersion7()}/cancel", owner, new { reason = "Unknown row" },
            HttpStatusCode.NotFound, "catalog.price_unknown");

        int auditedCancellations = await factory.WithServiceAsync(context => context.AuditLog
            .CountAsync(a => a.EntityId == product.Value && a.Action == AuditActions.Catalog.PriceCancelled));
        auditedCancellations.Should().Be(1);
        string? cancelReason = await factory.WithServiceAsync(async context =>
        {
            AuditLogEntry? byId = await context.AuditLog
                .Where(a => a.EntityId == product.Value && a.Action == AuditActions.Catalog.PriceCancelled)
                .OrderByDescending(a => a.OccurredAtUtc)
                .FirstOrDefaultAsync();
            return byId?.Reason;
        });
        cancelReason.Should().Be("Promo cancelled by buyer");
    }

    [Fact]
    public async Task LocationSettings_AreUpsertedInPlace_AndValidated()
    {
        ProductId product = await factory.CreateProductAsync("CUR-STOCK-01", "Bottled Water", "4800000300021");
        LocationId store = await factory.CreateLocationAsync("CUR-STOCK-S", "Stocking Store");
        LocationId external = await factory.CreateExternalLocationAsync("EXT-WRITEOFF");
        await factory.CreateUserAsync("cur-stock-mgr", Roles.MainInventoryManager);
        using HttpClient client = factory.CreateClient();

        string manager = await SignInAsync(client, "cur-stock-mgr");
        string setting = Path(product, "/location-settings/" + store.Value);

        await ExpectPutAsync(client, setting, manager, Thresholds(true, 5m, 10m, 20m, 30m, 12m), HttpStatusCode.NoContent);
        await ExpectPutAsync(client, setting, manager, Thresholds(false, 0m, 4m, 8m, 16m, 6m), HttpStatusCode.NoContent);
        await ExpectPutAsync(
            client, setting, manager, Thresholds(true, 9m, 1m, 8m, 16m, 6m),
            HttpStatusCode.BadRequest, "product_location.thresholds_unordered");
        await ExpectPutAsync(
            client, Path(product, "/location-settings/" + external.Value), manager, Thresholds(true, 0m, 1m, 2m, 3m, 1m),
            HttpStatusCode.Conflict, "catalog.location_unknown");

        using JsonDocument listed = await GetJsonAsync(client, Path(product, "/location-settings"), manager);
        JsonElement row = listed.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        row.GetProperty("isStocked").GetBoolean().Should().BeFalse();
        row.GetProperty("reorderPoint").GetDecimal().Should().Be(4m);
    }

    [Fact]
    public async Task UnitConversionsAndSupplierLinks_AreCurated()
    {
        ProductId product = await factory.CreateProductAsync("CUR-LINK-01", "Soy Sauce 1L", "4800000300038");
        UnitOfMeasureId caseUnit = await factory.CreateUnitOfMeasureAsync("CUR-CASE", "Case");
        SupplierId first = await factory.CreateSupplierAsync("CUR-SUP-1", "Condiments Trading");
        SupplierId second = await factory.CreateSupplierAsync("CUR-SUP-2", "Pantry Wholesale");
        await factory.CreateUserAsync("cur-link-mgr", Roles.MainInventoryManager);
        await factory.CreateUserAsync("cur-link-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();

        string manager = await SignInAsync(client, "cur-link-mgr");
        string cashier = await SignInAsync(client, "cur-link-cashier");
        string conversions = Path(product, "/unit-conversions");

        Guid baseUnit;
        using (JsonDocument detail = await GetJsonAsync(client, Path(product), manager))
        {
            baseUnit = detail.RootElement.GetProperty("baseUnitOfMeasureId").GetGuid();
        }

        object conversion = new { fromUnitId = caseUnit.Value, toUnitId = baseUnit, factor = 24m };
        Guid conversionId;

        using (HttpResponseMessage added = await SendAsync(client, HttpMethod.Post, conversions, manager, conversion))
        {
            added.StatusCode.Should().Be(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
            conversionId = (await ReadJsonAsync(added)).RootElement.GetProperty("id").GetGuid();
        }

        await ExpectAsync(client, conversions, manager, conversion, HttpStatusCode.Conflict, "catalog.conversion_exists");
        await ExpectAsync(
            client, conversions, manager, new { fromUnitId = Guid.CreateVersion7(), toUnitId = baseUnit, factor = 6m },
            HttpStatusCode.Conflict, "catalog.uom_unknown");

        await ExpectDeleteAsync(client, conversions + "/" + conversionId, manager, HttpStatusCode.NoContent);
        await ExpectDeleteAsync(client, conversions + "/" + conversionId, manager, HttpStatusCode.NotFound, "catalog.conversion_unknown");

        string suppliers = Path(product, "/suppliers");
        await ExpectPutAsync(
            client, suppliers + "/" + first.Value, manager, new { supplierSku = "CT-SOY-1L", leadTimeDays = 3, isPreferred = true },
            HttpStatusCode.NoContent);
        await ExpectPutAsync(
            client, suppliers + "/" + second.Value, manager, new { leadTimeDays = 5, isPreferred = true }, HttpStatusCode.NoContent);
        await ExpectPutAsync(
            client, suppliers + "/" + Guid.CreateVersion7(), manager, new { leadTimeDays = 1 },
            HttpStatusCode.Conflict, "catalog.supplier_unknown");

        using (JsonDocument links = await GetJsonAsync(client, suppliers, manager))
        {
            JsonElement[] rows = [.. links.RootElement.EnumerateArray()];
            rows.Should().HaveCount(2);
            rows.Should().ContainSingle(r => r.GetProperty("isPreferred").GetBoolean())
                .Which.GetProperty("supplierId").GetGuid().Should().Be(second.Value);
        }

        using (HttpResponseMessage hidden = await SendAsync(client, HttpMethod.Get, suppliers, cashier))
        {
            hidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "supplier links carry the last purchase cost");
        }

        await ExpectDeleteAsync(client, suppliers + "/" + first.Value, manager, HttpStatusCode.NoContent);
        await ExpectDeleteAsync(client, suppliers + "/" + first.Value, manager, HttpStatusCode.NotFound, "catalog.supplier_not_linked");
    }

    private static object Thresholds(
        bool isStocked, decimal minimum, decimal reorder, decimal target, decimal maximum, decimal preferred)
        => new
        {
            isStocked,
            minimumStock = minimum,
            reorderPoint = reorder,
            targetStock = target,
            maximumStock = maximum,
            preferredReplenishmentQuantity = preferred,
        };

    private static Task ExpectAsync(
        HttpClient client, string path, string token, object body, HttpStatusCode status, string? errorCode = null)
        => ExpectCoreAsync(client, HttpMethod.Post, path, token, body, status, errorCode);

    private static Task ExpectPutAsync(
        HttpClient client, string path, string token, object body, HttpStatusCode status, string? errorCode = null)
        => ExpectCoreAsync(client, HttpMethod.Put, path, token, body, status, errorCode);

    private static Task ExpectDeleteAsync(
        HttpClient client, string path, string token, HttpStatusCode status, string? errorCode = null)
        => ExpectCoreAsync(client, HttpMethod.Delete, path, token, body: null, status, errorCode);

    private static async Task ExpectCoreAsync(
        HttpClient client, HttpMethod method, string path, string token, object? body, HttpStatusCode status, string? errorCode)
    {
        using HttpResponseMessage response = await SendAsync(client, method, path, token, body);
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());

        if (errorCode is not null)
        {
            (await ReadErrorCodeAsync(response)).Should().Be(errorCode);
        }
    }
}
