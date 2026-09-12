using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The purchase order endpoints exercised through the real pipeline:
/// authentication, authorization, validation, the approval gate, the document
/// counter and the database.
/// </summary>
[Collection("api")]
public sealed class PurchaseOrderEndpointTests(PosApiFactory factory)
{
    // Product barcodes must be unique within the shared database. A process-wide
    // counter is the simplest way to guarantee that even if collections ever run
    // in parallel.
    private static long _barcodeSequence = 5900000000000;

    [Fact]
    public async Task FullLifecycle_DraftToOrdered()
    {
        Seed seed = await SeedAsync("life");
        await factory.CreateUserAsync("po-life-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("po-life-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-life-creator");
        string approver = await SignInAsync(client, "po-life-approver");

        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 10m, unitCost: 100m);

        (HttpStatusCode draftStatus, JsonElement draft) = await GetOrderAsync(client, creator, orderId);
        draftStatus.Should().Be(HttpStatusCode.OK);
        draft.GetProperty("status").GetString().Should().Be("Draft");
        draft.GetProperty("number").ValueKind.Should().Be(JsonValueKind.Null);
        draft.GetProperty("grandTotal").GetDecimal().Should().Be(1000m);

        using HttpResponseMessage submitted = await PostAsync(client, $"/api/v1/purchasing/orders/{orderId}/submit", creator);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());

        (HttpStatusCode _, JsonElement pending) = await GetOrderAsync(client, creator, orderId);
        pending.GetProperty("status").GetString().Should().Be("PendingApproval");
        string number = pending.GetProperty("number").GetString()!;
        number.Should().MatchRegex(@"^PO-\d{4}-\d{6}$");

        using HttpResponseMessage approved = await PostAsJsonAsync(
            client, $"/api/v1/purchasing/orders/{orderId}/approve", new { notes = "Within budget" }, approver);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());

        (HttpStatusCode _, JsonElement approvedDetail) = await GetOrderAsync(client, creator, orderId);
        approvedDetail.GetProperty("status").GetString().Should().Be("Approved");
        JsonElement decision = approvedDetail.GetProperty("approvals").EnumerateArray().Single();
        decision.GetProperty("decision").GetString().Should().Be("Approve");
        decision.GetProperty("thresholdApplied").GetDecimal().Should().Be(1000m);

        using HttpResponseMessage sent = await PostAsync(client, $"/api/v1/purchasing/orders/{orderId}/send", approver);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());

        (HttpStatusCode _, JsonElement ordered) = await GetOrderAsync(client, creator, orderId);
        ordered.GetProperty("status").GetString().Should().Be("Ordered");
        ordered.GetProperty("orderedAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task SubmittingTwice_IsRejected_AndDoesNotBurnASecondNumber()
    {
        Seed seed = await SeedAsync("twice");
        await factory.CreateUserAsync("po-twice-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-twice-creator");

        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 4m, unitCost: 25m);

        using HttpResponseMessage first = await PostAsync(client, $"/api/v1/purchasing/orders/{orderId}/submit", creator);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        (HttpStatusCode _, JsonElement afterFirst) = await GetOrderAsync(client, creator, orderId);
        string number = afterFirst.GetProperty("number").GetString()!;

        using HttpResponseMessage second = await PostAsync(client, $"/api/v1/purchasing/orders/{orderId}/submit", creator);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(second)).Should().Be("purchasing.invalid_state");

        (HttpStatusCode _, JsonElement afterSecond) = await GetOrderAsync(client, creator, orderId);
        afterSecond.GetProperty("number").GetString().Should().Be(number, "a rejected submit must not consume a sequence value");
    }

    [Fact]
    public async Task Numbers_AreStrictlyIncreasingPerSubmission()
    {
        Seed seed = await SeedAsync("seq");
        await factory.CreateUserAsync("po-seq-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-seq-creator");

        Guid first = await CreateOrderAsync(client, creator, seed, quantity: 1m, unitCost: 1m);
        Guid second = await CreateOrderAsync(client, creator, seed, quantity: 2m, unitCost: 2m);

        (await PostAsync(client, $"/api/v1/purchasing/orders/{first}/submit", creator)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await PostAsync(client, $"/api/v1/purchasing/orders/{second}/submit", creator)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (HttpStatusCode _, JsonElement firstDetail) = await GetOrderAsync(client, creator, first);
        (HttpStatusCode _, JsonElement secondDetail) = await GetOrderAsync(client, creator, second);

        long firstSequence = Sequence(firstDetail.GetProperty("number").GetString()!);
        long secondSequence = Sequence(secondDetail.GetProperty("number").GetString()!);
        secondSequence.Should().BeGreaterThan(firstSequence);
    }

    [Fact]
    public async Task SelfApproval_IsRefused()
    {
        Seed seed = await SeedAsync("self");
        await factory.CreateUserAsync("po-self-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-self-approver");

        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 5m, unitCost: 10m);
        (await PostAsync(client, $"/api/v1/purchasing/orders/{orderId}/submit", creator)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage approve = await PostAsync(
            client, $"/api/v1/purchasing/orders/{orderId}/approve", creator);

        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(approve)).Should().Be("approval.self_approval_refused");
    }

    [Fact]
    public async Task Approval_ByAUserWithNoTier_IsRefused()
    {
        Seed seed = await SeedAsync("tier");
        await factory.CreateUserAsync("po-tier-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("po-tier-approver", Roles.MainInventoryManager, tier: ApprovalTier.None);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-tier-creator");
        string approver = await SignInAsync(client, "po-tier-approver");

        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 5m, unitCost: 10m);
        (await PostAsync(client, $"/api/v1/purchasing/orders/{orderId}/submit", creator)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage approve = await PostAsync(
            client, $"/api/v1/purchasing/orders/{orderId}/approve", approver);

        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(approve)).Should().Be("approval.tier_exceeded");
    }

    [Fact]
    public async Task Reject_MarksTheOrderRejected()
    {
        Seed seed = await SeedAsync("rej");
        await factory.CreateUserAsync("po-rej-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("po-rej-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-rej-creator");
        string approver = await SignInAsync(client, "po-rej-approver");

        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 5m, unitCost: 10m);
        (await PostAsync(client, $"/api/v1/purchasing/orders/{orderId}/submit", creator)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage reject = await PostAsJsonAsync(
            client, $"/api/v1/purchasing/orders/{orderId}/reject", new { notes = "Price too high" }, approver);
        reject.StatusCode.Should().Be(HttpStatusCode.OK, await reject.Content.ReadAsStringAsync());

        (HttpStatusCode _, JsonElement detail) = await GetOrderAsync(client, creator, orderId);
        detail.GetProperty("status").GetString().Should().Be("Rejected");
        detail.GetProperty("approvals").EnumerateArray().Single()
            .GetProperty("decision").GetString().Should().Be("Reject");
    }

    [Fact]
    public async Task Cancel_OfASubmittedOrder_RequiresAReason()
    {
        Seed seed = await SeedAsync("cxl");
        await factory.CreateUserAsync("po-cxl-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-cxl-creator");

        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 5m, unitCost: 10m);
        (await PostAsync(client, $"/api/v1/purchasing/orders/{orderId}/submit", creator)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage withoutReason = await PostAsJsonAsync(
            client, $"/api/v1/purchasing/orders/{orderId}/cancel", new { reason = (string?)null }, creator);
        withoutReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(withoutReason)).Should().Be("purchasing.cancel_reason_required");

        using HttpResponseMessage withReason = await PostAsJsonAsync(
            client, $"/api/v1/purchasing/orders/{orderId}/cancel", new { reason = "Supplier went out of business" }, creator);
        withReason.StatusCode.Should().Be(HttpStatusCode.OK, await withReason.Content.ReadAsStringAsync());

        (HttpStatusCode _, JsonElement detail) = await GetOrderAsync(client, creator, orderId);
        detail.GetProperty("status").GetString().Should().Be("Cancelled");
        detail.GetProperty("cancelledReason").GetString().Should().Be("Supplier went out of business");
    }

    [Fact]
    public async Task Withdraw_DeletesADraft_ButNotASubmittedOrder()
    {
        Seed seed = await SeedAsync("wdr");
        await factory.CreateUserAsync("po-wdr-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-wdr-creator");

        Guid draft = await CreateOrderAsync(client, creator, seed, quantity: 1m, unitCost: 1m);
        using HttpResponseMessage withdrawn = await DeleteAsync(client, $"/api/v1/purchasing/orders/{draft}", creator);
        withdrawn.StatusCode.Should().Be(HttpStatusCode.OK, await withdrawn.Content.ReadAsStringAsync());

        (HttpStatusCode _, JsonElement withdrawnDetail) = await GetOrderAsync(client, creator, draft);
        withdrawnDetail.GetProperty("status").GetString().Should().Be("Cancelled");

        Guid submitted = await CreateOrderAsync(client, creator, seed, quantity: 2m, unitCost: 2m);
        (await PostAsync(client, $"/api/v1/purchasing/orders/{submitted}/submit", creator)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage refused = await DeleteAsync(client, $"/api/v1/purchasing/orders/{submitted}", creator);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(refused)).Should().Be("purchasing.invalid_state");
    }

    [Fact]
    public async Task Cashier_CannotCreateOrApprove()
    {
        Seed seed = await SeedAsync("cash");
        await factory.CreateUserAsync("po-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();
        string cashier = await SignInAsync(client, "po-cashier");

        using HttpResponseMessage create = await PostAsJsonAsync(client, "/api/v1/purchasing/orders", OrderBody(seed), cashier);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage approve = await PostAsync(
            client, $"/api/v1/purchasing/orders/{Guid.CreateVersion7()}/approve", cashier);
        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Auditor_CanListButNotCreate()
    {
        Seed seed = await SeedAsync("audit");
        await factory.CreateUserAsync("po-auditor", Roles.Auditor);
        using HttpClient client = factory.CreateClient();
        string auditor = await SignInAsync(client, "po-auditor");

        using HttpResponseMessage listed = await GetAsync(client, "/api/v1/purchasing/orders", auditor);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage create = await PostAsJsonAsync(client, "/api/v1/purchasing/orders", OrderBody(seed), auditor);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DuplicateProductLines_AreRejected()
    {
        Seed seed = await SeedAsync("dupe");
        await factory.CreateUserAsync("po-dupe-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-dupe-creator");

        object body = new
        {
            supplierId = seed.Supplier.Value,
            destinationLocationId = seed.Store.Value,
            lines = new object[]
            {
                new { productId = seed.Product.Value, unitOfMeasureId = seed.BaseUnit.Value, orderedQuantity = 10m, unitCost = 100m },
                new { productId = seed.Product.Value, unitOfMeasureId = seed.BaseUnit.Value, orderedQuantity = 5m, unitCost = 100m },
            },
        };

        using HttpResponseMessage response = await PostAsJsonAsync(client, "/api/v1/purchasing/orders", body, creator);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be("purchasing.duplicate_product_line");
    }

    [Fact]
    public async Task UnknownSupplier_IsNotFound()
    {
        Seed seed = await SeedAsync("nosup");
        await factory.CreateUserAsync("po-nosup-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-nosup-creator");

        object body = new
        {
            supplierId = Guid.CreateVersion7(),
            destinationLocationId = seed.Store.Value,
            lines = new object[]
            {
                new { productId = seed.Product.Value, unitOfMeasureId = seed.BaseUnit.Value, orderedQuantity = 1m, unitCost = 1m },
            },
        };

        using HttpResponseMessage response = await PostAsJsonAsync(client, "/api/v1/purchasing/orders", body, creator);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorCodeAsync(response)).Should().Be("purchasing.supplier_unknown");
    }

    [Fact]
    public async Task LineInANonBaseUnit_IsRejected()
    {
        Seed seed = await SeedAsync("uom");
        UnitOfMeasureId box = await factory.CreateUnitOfMeasureAsync("BOX", "Box");
        await factory.CreateUserAsync("po-uom-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-uom-creator");

        object body = new
        {
            supplierId = seed.Supplier.Value,
            destinationLocationId = seed.Store.Value,
            lines = new object[]
            {
                new { productId = seed.Product.Value, unitOfMeasureId = box.Value, orderedQuantity = 1m, unitCost = 1200m },
            },
        };

        using HttpResponseMessage response = await PostAsJsonAsync(client, "/api/v1/purchasing/orders", body, creator);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be("purchasing.line_uom_mismatch");
    }

    [Fact]
    public async Task InvalidCurrency_IsRejected()
    {
        Seed seed = await SeedAsync("curr");
        await factory.CreateUserAsync("po-curr-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "po-curr-creator");

        object body = new
        {
            supplierId = seed.Supplier.Value,
            destinationLocationId = seed.Store.Value,
            currencyCode = "php",
            lines = new object[]
            {
                new { productId = seed.Product.Value, unitOfMeasureId = seed.BaseUnit.Value, orderedQuantity = 1m, unitCost = 1m },
            },
        };

        using HttpResponseMessage response = await PostAsJsonAsync(client, "/api/v1/purchasing/orders", body, creator);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be("purchasing.currency_invalid");
    }

    private async Task<Seed> SeedAsync(string suffix)
    {
        SupplierId supplier = await factory.CreateSupplierAsync($"SUP-{suffix}", $"Supplier {suffix}");
        LocationId store = await factory.CreateLocationAsync($"PO-{suffix}", $"Store {suffix}");
        string barcode = Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture);
        ProductId product = await factory.CreateProductAsync($"PO-{suffix}", $"Product {suffix}", barcode);
        UnitOfMeasureId baseUnit = await factory.CreateUnitOfMeasureAsync("PC", "Piece");

        return new Seed(supplier, store, product, baseUnit);
    }

    private static object OrderBody(Seed seed, decimal quantity = 1m, decimal unitCost = 1m)
        => new
        {
            supplierId = seed.Supplier.Value,
            destinationLocationId = seed.Store.Value,
            lines = new object[]
            {
                new { productId = seed.Product.Value, unitOfMeasureId = seed.BaseUnit.Value, orderedQuantity = quantity, unitCost },
            },
        };

    private static async Task<Guid> CreateOrderAsync(
        HttpClient client,
        string accessToken,
        Seed seed,
        decimal quantity,
        decimal unitCost)
    {
        using HttpResponseMessage response = await PostAsJsonAsync(
            client,
            "/api/v1/purchasing/orders",
            OrderBody(seed, quantity, unitCost),
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Detail)> GetOrderAsync(
        HttpClient client,
        string accessToken,
        Guid orderId)
    {
        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}"),
            accessToken);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("errorCode", out JsonElement code)
            ? code.GetString()
            : null;
    }

    private static long Sequence(string number)
        => long.Parse(number[(number.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Delete, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative));
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

    private sealed record Seed(
        SupplierId Supplier,
        LocationId Store,
        ProductId Product,
        UnitOfMeasureId BaseUnit);
}