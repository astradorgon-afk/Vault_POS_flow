using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The catalog endpoints exercised through the real pipeline: authentication,
/// authorization, validation and the database.
/// </summary>
[Collection("api")]
public sealed class CatalogEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task Cashier_CanReadTheProductCatalogue()
    {
        await factory.CreateProductAsync("RICE-01", "Premium Rice 5kg", "4800000000017");
        await factory.CreateUserAsync("cat-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "cat-cashier");

        using HttpResponseMessage listed = await GetAsync(client, "/api/v1/catalog/products", token);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument listDocument = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        JsonElement productJson = listDocument.RootElement.EnumerateArray()
            .Should().ContainSingle(p => p.GetProperty("sku").GetString() == "RICE-01")
            .Subject;

        productJson.GetProperty("sku").GetString().Should().Be("RICE-01");
        productJson.GetProperty("barcodes").EnumerateArray().Select(b => b.GetString())
            .Should().Contain("4800000000017");

        Guid productId = productJson.GetProperty("id").GetGuid();

        using HttpResponseMessage byId = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/catalog/products/{productId}"),
            token);
        byId.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage byBarcode = await GetAsync(
            client,
            "/api/v1/catalog/products/by-barcode/4800000000017",
            token);
        byBarcode.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage filtered = await GetAsync(
            client,
            "/api/v1/catalog/products?q=rice",
            token);
        filtered.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadingAnUnknownProduct_ReturnsNotFound()
    {
        await factory.CreateProductAsync("COFFEE-200", "Ground Coffee 200g", "4800000000024");
        await factory.CreateUserAsync("cat-cashier-404", Roles.Cashier);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "cat-cashier-404");

        using HttpResponseMessage byId = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/catalog/products/{Guid.CreateVersion7()}"),
            token);
        byId.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using HttpResponseMessage byBarcode = await GetAsync(
            client,
            "/api/v1/catalog/products/by-barcode/9999999999999",
            token);
        byBarcode.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StoreManager_CannotCreateProducts()
    {
        CategoryId category = await factory.CreateCategoryAsync("GROCERIES", "Groceries");
        UnitOfMeasureId unit = await factory.CreateUnitOfMeasureAsync("PC", "Piece");
        await factory.CreateUserAsync("cat-manager", Roles.StoreManager);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "cat-manager");

        using HttpResponseMessage response = await PostAsJsonAsync(
            client,
            "/api/v1/catalog/products",
            new
            {
                sku = "SKIP-01",
                name = "Must Not Appear",
                categoryId = category.Value,
                baseUnitOfMeasureId = unit.Value,
            },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InventoryStaff_CanCreateProducts()
    {
        CategoryId category = await factory.CreateCategoryAsync("CAT-STAFF", "Staff Category");
        UnitOfMeasureId unit = await factory.CreateUnitOfMeasureAsync("PCS-STF", "Piece");
        await factory.CreateUserAsync("cat-staff", Roles.InventoryStaff);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "cat-staff");

        using HttpResponseMessage response = await PostAsJsonAsync(
            client,
            "/api/v1/catalog/products",
            new
            {
                sku = "STAFF-01",
                name = "Staff Created Product",
                categoryId = category.Value,
                baseUnitOfMeasureId = unit.Value,
            },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task MainInventoryManager_CanCreateAProductWithBarcode_ThenReadItBack()
    {
        CategoryId category = await factory.CreateCategoryAsync("BEVERAGES", "Beverages");
        UnitOfMeasureId unit = await factory.CreateUnitOfMeasureAsync("L", "Litre", UnitKind.Volume);
        await factory.CreateUserAsync("cat-mgr", Roles.MainInventoryManager);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "cat-mgr");

        using HttpResponseMessage created = await PostAsJsonAsync(
            client,
            "/api/v1/catalog/products",
            new
            {
                sku = "OIL-1L",
                name = "Cooking Oil 1L",
                categoryId = category.Value,
                baseUnitOfMeasureId = unit.Value,
                initialBarcode = "4800000000048",
            },
            token);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid productId = createdDocument.RootElement.GetProperty("id").GetGuid();
        productId.Should().NotBe(Guid.Empty);

        using HttpResponseMessage byBarcode = await GetAsync(
            client,
            "/api/v1/catalog/products/by-barcode/4800000000048",
            token);
        byBarcode.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument byBarcodeDocument = JsonDocument.Parse(await byBarcode.Content.ReadAsStringAsync());
        byBarcodeDocument.RootElement.GetProperty("id").GetGuid().Should().Be(productId);
        byBarcodeDocument.RootElement.GetProperty("sku").GetString().Should().Be("OIL-1L");
    }

    [Fact]
    public async Task CreatingADuplicateProduct_ReturnsConflict()
    {
        CategoryId category = await factory.CreateCategoryAsync("DAIRY", "Dairy");
        UnitOfMeasureId unit = await factory.CreateUnitOfMeasureAsync("PC", "Piece");
        await factory.CreateUserAsync("cat-mgr-dupe", Roles.MainInventoryManager);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "cat-mgr-dupe");

        object body = new
        {
            sku = "MILK-370",
            name = "Evaporated Milk 370ml",
            categoryId = category.Value,
            baseUnitOfMeasureId = unit.Value,
        };

        using HttpResponseMessage first = await PostAsJsonAsync(client, "/api/v1/catalog/products", body, token);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        using HttpResponseMessage second = await PostAsJsonAsync(client, "/api/v1/catalog/products", body, token);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
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