using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Locations;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The purchasing part-three endpoints exercised through the real pipeline:
/// direct-delivery authorizations, the supplier return lifecycle with its
/// ledger posting, and the receiving-discrepancy resolution.
/// </summary>
[Collection("api")]
public sealed class PurchasingPart3EndpointTests(PosApiFactory factory)
{
    // Product barcodes must be unique within the shared database. A process-wide
    // counter is the simplest way to guarantee that even if collections ever run
    // in parallel.
    private static long _barcodeSequence = 7300000000000;

    [Fact]
    public async Task DirectDeliveryAuthorization_IssueFilterAndRevoke()
    {
        Seed seed = await SeedAsync("dda");
        await factory.CreateUserAsync("dda-manager", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "dda-manager");

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using HttpResponseMessage issued = await PostAsJsonAsync(
            client,
            "/api/v1/purchasing/direct-delivery-authorizations",
            new
            {
                supplierId = seed.Supplier.Value,
                storeLocationId = seed.Store.Value,
                validFrom = today,
                validUntil = today.AddDays(90),
            },
            manager);
        issued.StatusCode.Should().Be(HttpStatusCode.OK, await issued.Content.ReadAsStringAsync());

        using JsonDocument issuedBody = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        Guid authorizationId = issuedBody.RootElement.GetProperty("id").GetGuid();

        // A second standing authorization for the same pairing is allowed: they
        // are separate grants that can be revoked independently.
        using HttpResponseMessage second = await PostAsJsonAsync(
            client,
            "/api/v1/purchasing/direct-delivery-authorizations",
            new
            {
                supplierId = seed.Supplier.Value,
                storeLocationId = seed.Store.Value,
                validFrom = today,
                validUntil = today.AddDays(180),
            },
            manager);
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());

        using HttpResponseMessage active = await GetAsync(
            client, "/api/v1/purchasing/direct-delivery-authorizations", manager);
        active.StatusCode.Should().Be(HttpStatusCode.OK);
        using (JsonDocument document = JsonDocument.Parse(await active.Content.ReadAsStringAsync()))
        {
            document.RootElement.GetArrayLength().Should().Be(2);
            JsonElement first = document.RootElement.EnumerateArray().First();
            first.GetProperty("status").GetString().Should().Be("Active");
            first.GetProperty("validFrom").GetString().Should().Be(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            first.GetProperty("validUntil").GetString().Should().Be(today.AddDays(180).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            // Newest first: the first listed authorization carries the longer window.
            JsonElement oldest = document.RootElement.EnumerateArray().Last();
            oldest.GetProperty("validUntil").GetString().Should().Be(today.AddDays(90).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        using HttpResponseMessage revoked = await PostAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/direct-delivery-authorizations/{authorizationId}/revoke"),
            manager);
        revoked.StatusCode.Should().Be(HttpStatusCode.OK, await revoked.Content.ReadAsStringAsync());

        using HttpResponseMessage onlyActive = await GetAsync(
            client, "/api/v1/purchasing/direct-delivery-authorizations", manager);
        using (JsonDocument document = JsonDocument.Parse(await onlyActive.Content.ReadAsStringAsync()))
        {
            document.RootElement.GetArrayLength().Should().Be(1);
        }

        using HttpResponseMessage all = await GetAsync(
            client, "/api/v1/purchasing/direct-delivery-authorizations?activeOnly=false", manager);
        using (JsonDocument document = JsonDocument.Parse(await all.Content.ReadAsStringAsync()))
        {
            document.RootElement.GetArrayLength().Should().Be(2);
            JsonElement revokedAuthorization = document.RootElement
                .EnumerateArray()
                .Single(x => x.GetProperty("status").GetString() == "Revoked");
            revokedAuthorization.GetProperty("revokedByUserId").ValueKind.Should().NotBe(JsonValueKind.Null);
            revokedAuthorization.GetProperty("revokedAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task SupplierReturn_FullLifecycle_DispatchesTheLedgerAgainstTheSrtNumber()
    {
        Seed seed = await SeedAsync("srt");
        await factory.CreateUserAsync("srt-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("srt-manager", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "srt-creator");
        string manager = await SignInAsync(client, "srt-manager");

        // First move ten units into Damaged at the store: returns source their
        // stock from what is actually on the floor.
        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, creator, manager, orderId);
        Guid lineId = await LineIdAsync(client, manager, orderId);
        Guid receiptId = await PostDamagedReceiptAsync(client, manager, orderId, lineId, received: 10m, damaged: 10m);

        (decimal postedSum, _, _) = await MovementTotalsAsync(receiptId);
        postedSum.Should().Be(0m);

        // Two draft returns prove the number stays unique once assigned: drafts
        // share the blank number and must not collide.
        Guid first = await CreateReturnAsync(client, creator, seed, sourceState: 6, quantity: 6m);
        Guid second = await CreateReturnAsync(client, creator, seed, sourceState: 6, quantity: 4m);

        using HttpResponseMessage draft = await GetReturnAsync(client, creator, first);
        JsonElement draftBody = JsonDocument.Parse(await draft.Content.ReadAsStringAsync()).RootElement.Clone();
        draftBody.GetProperty("status").GetString().Should().Be("Draft");
        draftBody.GetProperty("number").GetString().Should().BeEmpty();

        // The approver must be someone other than the creator: that separation
        // of duties is the same gate the approve endpoint enforces for orders.
        using HttpResponseMessage submitted = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/purchasing/supplier-returns/{first}/submit"), creator);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());

        using HttpResponseMessage approved = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/purchasing/supplier-returns/{first}/approve"), manager);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());

        using HttpResponseMessage dispatched = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/supplier-returns/{first}/dispatch"),
            new { supplierAuthorizationNumber = "RA-1001" },
            manager);
        dispatched.StatusCode.Should().Be(HttpStatusCode.OK, await dispatched.Content.ReadAsStringAsync());

        // Six units left Damaged and reached the external counterparty, with the
        // SRT number stamped on both legs.
        List<MovementSnapshot> movements = await MovementSnapshotsAsync(first);
        movements.Should().HaveCount(2);
        movements.Sum(m => m.QuantityDelta).Should().Be(0m);
        movements.Single(m => m.State == "Damaged").QuantityDelta.Should().Be(-6m);
        movements.Single(m => m.State == "External").QuantityDelta.Should().Be(6m);
        movements.Should().OnlyContain(m => Regex.IsMatch(m.ReferenceNumber, @"^SRT-\d{4}-\d{6}$"));

        using HttpResponseMessage dispatchedDetailResponse = await GetReturnAsync(client, manager, first);
        JsonElement afterDispatch = JsonDocument.Parse(await dispatchedDetailResponse.Content.ReadAsStringAsync()).RootElement.Clone();
        afterDispatch.GetProperty("status").GetString().Should().Be("Dispatched");
        afterDispatch.GetProperty("number").GetString()!.Should().MatchRegex(@"^SRT-\d{4}-\d{6}$");
        afterDispatch.GetProperty("approvedByUserId").ValueKind.Should().NotBe(JsonValueKind.Null);
        afterDispatch.GetProperty("supplierAuthorizationNumber").GetString().Should().Be("RA-1001");
        afterDispatch.GetProperty("dispatchedAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);

        using HttpResponseMessage confirmed = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/purchasing/supplier-returns/{first}/confirm"), manager);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());

        using HttpResponseMessage confirmedDetailResponse = await GetReturnAsync(client, manager, first);
        JsonDocument.Parse(await confirmedDetailResponse.Content.ReadAsStringAsync()).RootElement
            .GetProperty("status").GetString().Should().Be("Confirmed");

        // The second draft stayed untouched by the first return's lifecycle.
        using HttpResponseMessage secondDetailResponse = await GetReturnAsync(client, manager, second);
        JsonDocument.Parse(await secondDetailResponse.Content.ReadAsStringAsync()).RootElement
            .GetProperty("status").GetString().Should().Be("Draft");
    }

    [Fact]
    public async Task SupplierReturn_DispatchBeforeApproval_IsRejected_AndPostsNothing()
    {
        Seed seed = await SeedAsync("srts");
        await factory.CreateUserAsync("srts-manager", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "srts-manager");

        Guid returnId = await CreateReturnAsync(client, manager, seed, sourceState: 6, quantity: 5m);

        using HttpResponseMessage dispatched = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/supplier-returns/{returnId}/dispatch"),
            new { supplierAuthorizationNumber = "RA-1002" },
            manager);

        dispatched.StatusCode.Should().Be(HttpStatusCode.Conflict, await dispatched.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(dispatched)).Should().Be("purchasing.return_invalid_state");

        (await factory.WithServiceAsync(context => context.InventoryMovements
            .CountAsync(m => m.ReferenceDocumentId == returnId))).Should().Be(0);
    }

    [Fact]
    public async Task SupplierReturn_CannotSourceFromAvailableStock()
    {
        Seed seed = await SeedAsync("srta");
        await factory.CreateUserAsync("srta-manager", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "srta-manager");

        using HttpResponseMessage created = await PostAsJsonAsync(
            client,
            "/api/v1/purchasing/supplier-returns",
            ReturnBody(seed, sourceState: 0, quantity: 5m), // Available is not returnable.
            manager);
        created.StatusCode.Should().Be(HttpStatusCode.BadRequest, await created.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(created)).Should().Be("purchasing.return_source_state_invalid");

        // Nothing was stored under this supplier.
        (await factory.WithServiceAsync(context => context.SupplierReturns
            .CountAsync(r => r.SupplierId == seed.Supplier))).Should().Be(0);
    }

    [Fact]
    public async Task Cashier_CannotCreateASupplierReturn()
    {
        await factory.CreateUserAsync("srtr-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();
        string cashier = await SignInAsync(client, "srtr-cashier");

        using HttpResponseMessage created = await PostAsJsonAsync(
            client,
            "/api/v1/purchasing/supplier-returns",
            new
            {
                supplierId = Guid.Empty,
                locationId = Guid.Empty,
                lines = new object[]
                {
                    new { productId = Guid.Empty, batchId = (Guid?)null, sourceState = 6, quantity = 1m, unitCost = 1m, reason = 2 },
                },
            },
            cashier);

        created.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReceivingDiscrepancy_ResolvedThroughThePipeline_AndOnlyOnce()
    {
        Seed seed = await SeedAsync("rslv");
        await factory.CreateUserAsync("rslv-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("rslv-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "rslv-creator");
        string approver = await SignInAsync(client, "rslv-approver");

        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, creator, approver, orderId);
        Guid lineId = await LineIdAsync(client, approver, orderId);

        // Four of ten arrived: a six-unit shortage opens on the receipt.
        using HttpResponseMessage posted = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 4m),
            approver);
        posted.StatusCode.Should().Be(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await posted.Content.ReadAsStringAsync());
        Guid receiptId = created.RootElement.GetProperty("id").GetGuid();

        (HttpStatusCode _, JsonElement before) = await GetReceiptAsync(client, approver, orderId, receiptId);
        JsonElement discrepancy = before.GetProperty("discrepancies").EnumerateArray().Single();
        Guid discrepancyId = discrepancy.GetProperty("id").GetGuid();
        discrepancy.GetProperty("kind").GetString().Should().Be("Shortage");
        discrepancy.GetProperty("resolutionOutcome").ValueKind.Should().Be(JsonValueKind.Null);

        using HttpResponseMessage resolved = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/receiving-discrepancies/{discrepancyId}/resolve"),
            new { outcome = 1, note = "Supplier agreed a credit note." },
            approver);
        resolved.StatusCode.Should().Be(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());

        (HttpStatusCode _, JsonElement detail) = await GetReceiptAsync(client, approver, orderId, receiptId);
        JsonElement closed = detail.GetProperty("discrepancies").EnumerateArray().Single();
        closed.GetProperty("resolutionOutcome").GetString().Should().Be("SupplierCredit");
        closed.GetProperty("resolutionNote").GetString().Should().Be("Supplier agreed a credit note.");
        closed.GetProperty("resolvedByUserId").ValueKind.Should().NotBe(JsonValueKind.Null);
        closed.GetProperty("resolvedAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);

        using HttpResponseMessage again = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/receiving-discrepancies/{discrepancyId}/resolve"),
            new { outcome = 4 },
            approver);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict, await again.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(again)).Should().Be("purchasing.discrepancy_already_resolved");
    }

    [Fact]
    public async Task ReceivingDiscrepancy_ResolveWithoutPermission_IsForbidden()
    {
        Seed seed = await SeedAsync("rslvf");
        await factory.CreateUserAsync("rslvf-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("rslvf-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        // A store manager may receive and view at their store but never close a
        // discrepancy: that decision belongs to purchasing management.
        await factory.CreateUserAsync("rslvf-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "rslvf-creator");
        string approver = await SignInAsync(client, "rslvf-approver");
        string store = await SignInAsync(client, "rslvf-store");

        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, creator, approver, orderId);
        Guid lineId = await LineIdAsync(client, store, orderId);

        using HttpResponseMessage posted = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 6m),
            store);
        posted.StatusCode.Should().Be(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync());

        // The route-level permission check refuses someone without the resolve
        // permission before any handler logic runs.
        using HttpResponseMessage resolved = await PostAsJsonAsync(
            client,
            "/api/v1/purchasing/receiving-discrepancies/00000000-0000-0000-0000-000000000000/resolve",
            new { outcome = 4 },
            store);

        resolved.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<Seed> SeedAsync(string suffix)
    {
        SupplierId supplier = await factory.CreateSupplierAsync($"SUP-{suffix}", $"Supplier {suffix}");
        LocationId store = await factory.CreateLocationAsync($"ST-{suffix}", $"Store {suffix}");
        string barcode = Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture);
        ProductId product = await factory.CreateProductAsync($"SR-{suffix}", $"Product {suffix}", barcode);
        UnitOfMeasureId baseUnit = await factory.CreateUnitOfMeasureAsync("PC", "Piece");

        return new Seed(supplier, store, product, baseUnit);
    }

    private static object ReturnBody(Seed seed, int sourceState, decimal quantity, Guid? batchId = null)
        => new
        {
            supplierId = seed.Supplier.Value,
            locationId = seed.Store.Value,
            lines = new object[]
            {
                new
                {
                    productId = seed.Product.Value,
                    batchId,
                    sourceState,
                    quantity,
                    unitCost = 100m,
                    reason = 2,
                },
            },
        };

    private static async Task<Guid> CreateReturnAsync(
        HttpClient client, string accessToken, Seed seed, int sourceState, decimal quantity)
    {
        using HttpResponseMessage response = await PostAsJsonAsync(
            client, "/api/v1/purchasing/supplier-returns", ReturnBody(seed, sourceState, quantity), accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static object ReceiptBody(Guid purchaseOrderLineId, decimal received, decimal damaged = 0m)
        => new
        {
            lines = new object[]
            {
                new
                {
                    purchaseOrderLineId,
                    quantityReceived = received,
                    quantityDamaged = damaged,
                    quantityWrongItem = 0m,
                    quantityExpired = 0m,
                    unitCost = 100m,
                },
            },
        };

    private static object OrderBody(Seed seed, decimal quantity, decimal unitCost)
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
        HttpClient client, string accessToken, Seed seed, decimal quantity, decimal unitCost)
    {
        using HttpResponseMessage response = await PostAsJsonAsync(
            client, "/api/v1/purchasing/orders", OrderBody(seed, quantity, unitCost), accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> LineIdAsync(HttpClient client, string accessToken, Guid orderId)
    {
        (HttpStatusCode _, JsonElement detail) = await GetOrderAsync(client, accessToken, orderId);
        return detail.GetProperty("lines").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private static async Task SendOrderAsync(
        HttpClient client, string creator, string approver, Guid orderId)
    {
        using HttpResponseMessage submitted = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/submit"), creator);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());

        using HttpResponseMessage approved = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/approve"),
            new { notes = "Within budget" },
            approver);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());

        using HttpResponseMessage sent = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/send"), approver);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> PostDamagedReceiptAsync(
        HttpClient client, string accessToken, Guid orderId, Guid lineId, decimal received, decimal damaged)
    {
        using HttpResponseMessage response = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received, damaged),
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Detail)> GetOrderAsync(
        HttpClient client, string accessToken, Guid orderId)
    {
        using HttpResponseMessage response = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}"), accessToken);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Detail)> GetReceiptAsync(
        HttpClient client, string accessToken, Guid orderId, Guid receiptId)
    {
        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts/{receiptId}"),
            accessToken);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task<HttpResponseMessage> GetReturnAsync(
        HttpClient client, string accessToken, Guid returnId)
        => await GetAsync(
            client, FormattableString.Invariant($"/api/v1/purchasing/supplier-returns/{returnId}"), accessToken);

    private async Task<(decimal Sum, int Legs, string ReferenceNumber)> MovementTotalsAsync(Guid referenceDocumentId)
    {
        List<MovementSnapshot> movements = await MovementSnapshotsAsync(referenceDocumentId);
        return (movements.Sum(m => m.QuantityDelta), movements.Count, movements[0].ReferenceNumber);
    }

    private async Task<List<MovementSnapshot>> MovementSnapshotsAsync(Guid referenceDocumentId)
        => await factory.WithServiceAsync(context => context.InventoryMovements
            .Where(m => m.ReferenceDocumentId == referenceDocumentId)
            .OrderBy(m => m.QuantityDelta)
            .Select(m => new MovementSnapshot(m.QuantityDelta, m.State.ToString(), m.ReferenceNumber))
            .ToListAsync());

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
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
        HttpClient client, string path, object body, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("errorCode", out JsonElement code)
            ? code.GetString()
            : null;
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

    private sealed record MovementSnapshot(decimal QuantityDelta, string State, string ReferenceNumber);

    private sealed record Seed(
        SupplierId Supplier,
        LocationId Store,
        ProductId Product,
        UnitOfMeasureId BaseUnit);
}