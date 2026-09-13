using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using static Pos.Api.IntegrationTests.UserAdministrationEndpointTests;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Curating an existing product through the real pipeline: editing it, who may
/// see its cost, the barcode lifecycle and deactivation.
/// </summary>
/// <remarks>
/// A retired barcode must stop scanning everywhere a code resolves a product,
/// yet stay reserved so it is never handed to another product: a re-pointed code
/// silently corrupts stock and revenue for two products at once.
/// </remarks>
[Collection("api")]
public sealed class ProductCurationEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task EditingAProduct_ChangesItsDetails_AuditsTheCostSeparately_AndHidesCostFromCashiers()
    {
        ProductId product = await factory.CreateProductAsync("CUR-EDIT-01", "Corn Chips", "4800000200017", defaultPurchaseCost: 10m);
        CategoryId snacks = await factory.CreateCategoryAsync("CUR-SNACKS", "Snacks");
        await factory.CreateUserAsync("cur-edit-mgr", Roles.MainInventoryManager);
        await factory.CreateUserAsync("cur-edit-store", Roles.StoreManager);
        await factory.CreateUserAsync("cur-edit-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();

        string manager = await SignInAsync(client, "cur-edit-mgr");
        string storeManager = await SignInAsync(client, "cur-edit-store");
        string cashier = await SignInAsync(client, "cur-edit-cashier");
        object edit = new
        {
            name = "Corn Chips 150g",
            categoryId = snacks.Value,
            description = "Lightly salted",
            defaultPurchaseCost = 12.5m,
        };

        using (HttpResponseMessage refused = await SendAsync(client, HttpMethod.Put, Path(product), storeManager, edit))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (HttpResponseMessage edited = await SendAsync(client, HttpMethod.Put, Path(product), manager, edit))
        {
            edited.StatusCode.Should().Be(HttpStatusCode.NoContent, await edited.Content.ReadAsStringAsync());
        }

        using (JsonDocument detail = await GetJsonAsync(client, Path(product), manager))
        {
            detail.RootElement.GetProperty("name").GetString().Should().Be("Corn Chips 150g");
            detail.RootElement.GetProperty("description").GetString().Should().Be("Lightly salted");
            detail.RootElement.GetProperty("categoryId").GetGuid().Should().Be(snacks.Value);
            detail.RootElement.GetProperty("defaultPurchaseCost").GetDecimal().Should().Be(12.5m);
        }

        using (JsonDocument cashierView = await GetJsonAsync(client, Path(product), cashier))
        {
            cashierView.RootElement.GetProperty("defaultPurchaseCost").ValueKind.Should().Be(JsonValueKind.Null,
                "a cashier holds product.view but not product.cost.view");
        }

        using (HttpResponseMessage unknownCategory = await SendAsync(
            client, HttpMethod.Put, Path(product), manager, new { name = "X", categoryId = Guid.CreateVersion7() }))
        {
            unknownCategory.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadErrorCodeAsync(unknownCategory)).Should().Be("catalog.category_unknown");
        }

        List<string> actions = await factory.WithServiceAsync(context => context.AuditLog
            .Where(a => a.EntityId == product.Value)
            .Select(a => a.Action)
            .ToListAsync());
        actions.Should().Contain([AuditActions.Catalog.ProductUpdated, AuditActions.Catalog.CostChanged]);
    }

    [Fact]
    public async Task BarcodeLifecycle_PromotesAndRetires_AndARetiredCodeStopsScanningButStaysReserved()
    {
        ProductId product = await factory.CreateProductAsync("CUR-CODE-01", "Instant Noodles", "4800000200024");
        ProductId other = await factory.CreateProductAsync("CUR-CODE-02", "Instant Noodles Spicy", "4800000200031");
        await factory.CreateUserAsync("cur-code-mgr", Roles.MainInventoryManager);
        await factory.CreateUserAsync("cur-code-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();

        string manager = await SignInAsync(client, "cur-code-mgr");
        string cashier = await SignInAsync(client, "cur-code-cashier");
        string barcodes = Path(product, "/barcodes");

        using (HttpResponseMessage refused = await SendAsync(
            client, HttpMethod.Post, barcodes, cashier, new { barcode = "4800000200048" }))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (HttpResponseMessage added = await SendAsync(
            client, HttpMethod.Post, barcodes, manager, new { barcode = "4800000200048", packQuantity = 12m, isPrimary = true }))
        {
            added.StatusCode.Should().Be(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        }

        using (JsonDocument listed = await GetJsonAsync(client, barcodes, manager))
        {
            listed.RootElement.EnumerateArray()
                .Select(b => (b.GetProperty("barcode").GetString(), b.GetProperty("isPrimary").GetBoolean()))
                .Should().Equal(("4800000200048", true), ("4800000200024", false));
        }

        using (HttpResponseMessage noReason = await SendAsync(
            client, HttpMethod.Post, barcodes + "/4800000200048/retire", manager, new { reason = (string?)null }))
        {
            noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorCodeAsync(noReason)).Should().Be("catalog.reason_required");
        }

        using (HttpResponseMessage retired = await SendAsync(
            client, HttpMethod.Post, barcodes + "/4800000200048/retire", manager, new { reason = "Case barcode replaced by supplier" }))
        {
            retired.StatusCode.Should().Be(HttpStatusCode.NoContent, await retired.Content.ReadAsStringAsync());
        }

        using (JsonDocument listed = await GetJsonAsync(client, barcodes, manager))
        {
            JsonElement[] rows = [.. listed.RootElement.EnumerateArray()];
            rows[0].GetProperty("barcode").GetString().Should().Be("4800000200024");
            rows[0].GetProperty("isPrimary").GetBoolean().Should().BeTrue("the remaining code is promoted");
            rows[1].GetProperty("retiredAtUtc").ValueKind.Should().Be(JsonValueKind.String);
        }

        using (HttpResponseMessage scanned = await SendAsync(
            client, HttpMethod.Get, "/api/v1/catalog/products/by-barcode/4800000200048", cashier))
        {
            scanned.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ReadErrorCodeAsync(scanned)).Should().Be("catalog.barcode_unknown");
        }

        using (JsonDocument summary = await GetJsonAsync(client, Path(product), cashier))
        {
            summary.RootElement.GetProperty("barcodes").EnumerateArray().Select(b => b.GetString())
                .Should().Equal("4800000200024");
        }

        await ExpectConflictAsync(client, barcodes, manager, "4800000200048", "catalog.barcode_retired");
        await ExpectConflictAsync(client, Path(other, "/barcodes"), manager, "4800000200048", "catalog.barcode_already_attached");
        await ExpectConflictAsync(client, Path(other, "/barcodes"), manager, "4800000200024", "catalog.barcode_already_attached");

        int audited = await factory.WithServiceAsync(context => context.AuditLog
            .CountAsync(a => a.EntityId == product.Value && a.Action == AuditActions.Catalog.BarcodeChanged));
        audited.Should().Be(2);
    }

    [Fact]
    public async Task Deactivation_RequiresAReason_HidesTheProductFromTheDefaultList_AndCanBeUndone()
    {
        ProductId product = await factory.CreateProductAsync("CUR-ACT-01", "Discontinued Soda", "4800000200055");
        await factory.CreateUserAsync("cur-act-mgr", Roles.MainInventoryManager);
        using HttpClient client = factory.CreateClient();

        string manager = await SignInAsync(client, "cur-act-mgr");

        using (HttpResponseMessage noReason = await SendAsync(client, HttpMethod.Post, Path(product, "/deactivate"), manager, new { }))
        {
            noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorCodeAsync(noReason)).Should().Be("catalog.reason_required");
        }

        using (HttpResponseMessage deactivated = await SendAsync(
            client, HttpMethod.Post, Path(product, "/deactivate"), manager, new { reason = "Supplier discontinued the line" }))
        {
            deactivated.StatusCode.Should().Be(HttpStatusCode.NoContent, await deactivated.Content.ReadAsStringAsync());
        }

        using (JsonDocument active = await GetJsonAsync(client, "/api/v1/catalog/products?q=CUR-ACT-01", manager))
        {
            active.RootElement.GetArrayLength().Should().Be(0);
        }

        using (JsonDocument all = await GetJsonAsync(client, "/api/v1/catalog/products?q=CUR-ACT-01&includeInactive=true", manager))
        {
            JsonElement row = all.RootElement.EnumerateArray().Should().ContainSingle().Subject;
            row.GetProperty("isActive").GetBoolean().Should().BeFalse();
            row.GetProperty("discontinuedOn").ValueKind.Should().Be(JsonValueKind.String);
        }

        using (HttpResponseMessage again = await SendAsync(
            client, HttpMethod.Post, Path(product, "/deactivate"), manager, new { reason = "Pressed twice by mistake" }))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadErrorCodeAsync(again)).Should().Be("catalog.product_already_inactive");
        }

        using (HttpResponseMessage activated = await SendAsync(client, HttpMethod.Post, Path(product, "/activate"), manager))
        {
            activated.StatusCode.Should().Be(HttpStatusCode.NoContent, await activated.Content.ReadAsStringAsync());
        }

        using JsonDocument detail = await GetJsonAsync(client, Path(product), manager);
        detail.RootElement.GetProperty("isActive").GetBoolean().Should().BeTrue();
        detail.RootElement.GetProperty("discontinuedOn").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CuratingAnUnknownProduct_ReturnsNotFound()
    {
        await factory.CreateUserAsync("cur-404-mgr", Roles.MainInventoryManager);
        using HttpClient client = factory.CreateClient();

        string manager = await SignInAsync(client, "cur-404-mgr");
        ProductId unknown = new(Guid.CreateVersion7());

        using HttpResponseMessage listed = await SendAsync(client, HttpMethod.Get, Path(unknown, "/barcodes"), manager);
        listed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorCodeAsync(listed)).Should().Be("catalog.product_unknown");

        using HttpResponseMessage added = await SendAsync(
            client, HttpMethod.Post, Path(unknown, "/barcodes"), manager, new { barcode = "4800000200062" });
        added.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorCodeAsync(added)).Should().Be("catalog.product_unknown");
    }

    internal static string Path(ProductId product, string suffix = "")
        => FormattableString.Invariant($"/api/v1/catalog/products/{product.Value}{suffix}");

    private static async Task ExpectConflictAsync(
        HttpClient client, string path, string accessToken, string barcode, string errorCode)
    {
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, path, accessToken, new { barcode });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(response)).Should().Be(errorCode);
    }
}
