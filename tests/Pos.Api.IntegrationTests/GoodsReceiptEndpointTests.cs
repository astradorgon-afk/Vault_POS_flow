using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Locations;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The goods receipt endpoints exercised through the real pipeline: receiving
/// permission enforcement, disposition planning, cost-variance approval, the GRN
/// counter and the ledger posting.
/// </summary>
[Collection("api")]
public sealed class GoodsReceiptEndpointTests(PosApiFactory factory)
{
    // Product barcodes must be unique within the shared database. A process-wide
    // counter is the simplest way to guarantee that even if collections ever run
    // in parallel.
    private static long _barcodeSequence = 7200000000000;

    [Fact]
    public async Task FullReceipt_PostsTheGrn_AndCompletesTheOrder()
    {
        Seed seed = await SeedAsync("grnf");
        await factory.CreateUserAsync("grnf-rec", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grnf-app", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string receiver = await SignInAsync(client, "grnf-rec");
        string approver = await SignInAsync(client, "grnf-app");

        Guid orderId = await CreateOrderAsync(client, receiver, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, receiver, approver, orderId);
        Guid lineId = await LineIdAsync(client, receiver, orderId);

        using HttpResponseMessage post = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 10m, unitCost: 100m),
            receiver);
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Guid receiptId = created.RootElement.GetProperty("id").GetGuid();

        (HttpStatusCode _, JsonElement detail) = await GetReceiptAsync(client, receiver, orderId, receiptId);
        detail.GetProperty("number").GetString()!.Should().MatchRegex(@"^GRN-\d{4}-\d{6}$");
        detail.GetProperty("status").GetString().Should().Be("Posted");
        detail.GetProperty("costVariancePendingApproval").GetBoolean().Should().BeFalse();
        detail.GetProperty("costVarianceValueAtStake").GetDecimal().Should().Be(0m);

        JsonElement line = detail.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("quantityExpected").GetDecimal().Should().Be(10m);
        line.GetProperty("quantityReceived").GetDecimal().Should().Be(10m);
        line.GetProperty("quantityAccepted").GetDecimal().Should().Be(10m);
        line.GetProperty("acceptedState").GetString().Should().Be("PendingInspection");
        detail.GetProperty("discrepancies").GetArrayLength().Should().Be(0);

        // The ledger moved ten out of the supplier and ten into the store, with
        // the receipt's GRN number on both legs.
        (decimal sum, int legs, string referenceNumber) = await MovementTotalsAsync(receiptId);
        legs.Should().Be(2);
        sum.Should().Be(0m);
        referenceNumber.Should().MatchRegex(@"^GRN-\d{4}-\d{6}$");

        (HttpStatusCode _, JsonElement order) = await GetOrderAsync(client, receiver, orderId);
        order.GetProperty("status").GetString().Should().Be("FullyReceived");
    }

    [Fact]
    public async Task PartialReceipt_RecordsTheShortage_AndMarksTheOrderPartiallyReceived()
    {
        Seed seed = await SeedAsync("grnp");
        await factory.CreateUserAsync("grnp-rec", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grnp-app", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string receiver = await SignInAsync(client, "grnp-rec");
        string approver = await SignInAsync(client, "grnp-app");

        Guid orderId = await CreateOrderAsync(client, receiver, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, receiver, approver, orderId);
        Guid lineId = await LineIdAsync(client, receiver, orderId);

        using HttpResponseMessage post = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 4m, unitCost: 100m),
            receiver);
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Guid receiptId = created.RootElement.GetProperty("id").GetGuid();

        (HttpStatusCode _, JsonElement detail) = await GetReceiptAsync(client, receiver, orderId, receiptId);
        JsonElement discrepancy = detail.GetProperty("discrepancies").EnumerateArray().Single();
        discrepancy.GetProperty("kind").GetString().Should().Be("Shortage");
        discrepancy.GetProperty("quantity").GetDecimal().Should().Be(6m);
        discrepancy.GetProperty("valueImpact").GetDecimal().Should().Be(600m);

        (decimal sum, int legs, _) = await MovementTotalsAsync(receiptId);
        legs.Should().Be(2);
        sum.Should().Be(0m);

        (HttpStatusCode _, JsonElement order) = await GetOrderAsync(client, receiver, orderId);
        order.GetProperty("status").GetString().Should().Be("PartiallyReceived");
    }

    [Fact]
    public async Task OverageBeyondTolerance_PutsTheExcessOnHoldInQuarantine()
    {
        Seed seed = await SeedAsync("grno");
        await factory.CreateUserAsync("grno-rec", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grno-app", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string receiver = await SignInAsync(client, "grno-rec");
        string approver = await SignInAsync(client, "grno-app");

        Guid orderId = await CreateOrderAsync(client, receiver, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, receiver, approver, orderId);
        Guid lineId = await LineIdAsync(client, receiver, orderId);

        using HttpResponseMessage post = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 12m, unitCost: 100m),
            receiver);
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Guid receiptId = created.RootElement.GetProperty("id").GetGuid();

        (HttpStatusCode _, JsonElement detail) = await GetReceiptAsync(client, receiver, orderId, receiptId);
        JsonElement line = detail.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("quantityAccepted").GetDecimal().Should().Be(10.5m);
        line.GetProperty("overageBeyondTolerance").GetDecimal().Should().Be(1.5m);

        JsonElement overage = detail.GetProperty("discrepancies").EnumerateArray().Single();
        overage.GetProperty("kind").GetString().Should().Be("Overage");
        overage.GetProperty("quantity").GetDecimal().Should().Be(1.5m);

        // Three legs: supplier out, accepted in, excess quarantined.
        List<MovementSnapshot> movements = await MovementSnapshotsAsync(receiptId);
        movements.Select(m => m.State).Should().BeEquivalentTo("External", "PendingInspection", "Quarantine");
        movements.Sum(m => m.QuantityDelta).Should().Be(0m);
        movements.Single(m => m.State == "Quarantine").QuantityDelta.Should().Be(1.5m);
    }

    [Fact]
    public async Task CostVarianceBeyondTolerance_WithoutApprovalAuthority_IsRefused_AndPostsNothing()
    {
        Seed seed = await SeedAsync("grnv");
        LocationId external = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        await factory.CreateUserAsync("grnv-manager", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grnv-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        // A store staff member may receive at their location but never
        // self-approve a cost variance: that is the segregation of duties.
        await factory.CreateUserAsync(
            "grnv-staff", Roles.InventoryStaff, locations: [seed.Store], tier: ApprovalTier.None);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "grnv-manager");
        string approver = await SignInAsync(client, "grnv-approver");
        string staff = await SignInAsync(client, "grnv-staff");

        Guid orderId = await CreateOrderAsync(client, manager, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, manager, approver, orderId);
        Guid lineId = await LineIdAsync(client, staff, orderId);

        using HttpResponseMessage post = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 10m, unitCost: 110m),
            staff);
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden, await post.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(post)).Should().Be("purchasing.cost_variance_requires_approval");

        // Nothing was written: no receipt, no GRN, no movement, order untouched.
        (await ReceiptCountAsync(orderId)).Should().Be(0);
        (HttpStatusCode _, JsonElement order) = await GetOrderAsync(client, manager, orderId);
        order.GetProperty("status").GetString().Should().Be("Ordered");

        // Delivering at the ordered cost is within the staff member's authority.
        using HttpResponseMessage clean = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 10m, unitCost: 100m),
            staff);
        clean.StatusCode.Should().Be(HttpStatusCode.OK, await clean.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CostVarianceBeyondTolerance_WithAuthority_IsGranted_AndRecordsTheApprover()
    {
        Seed seed = await SeedAsync("grna");
        // The receiver must not be the person who raised the order: the approval
        // gate refuses the order creator from signing off their own document.
        await factory.CreateUserAsync("grna-creator", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grna-rec", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grna-app", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string creator = await SignInAsync(client, "grna-creator");
        string receiver = await SignInAsync(client, "grna-rec");
        string approver = await SignInAsync(client, "grna-app");

        Guid orderId = await CreateOrderAsync(client, creator, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, creator, approver, orderId);
        Guid lineId = await LineIdAsync(client, receiver, orderId);

        using HttpResponseMessage post = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 10m, unitCost: 110m),
            receiver);
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Guid receiptId = created.RootElement.GetProperty("id").GetGuid();

        (HttpStatusCode _, JsonElement detail) = await GetReceiptAsync(client, receiver, orderId, receiptId);
        detail.GetProperty("costVariancePendingApproval").GetBoolean().Should().BeTrue();
        detail.GetProperty("costVarianceValueAtStake").GetDecimal().Should().Be(1100m);

        JsonElement line = detail.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("costVariancePercent").GetDecimal().Should().Be(10m);
        line.GetProperty("costVarianceApprovedByUserId").ValueKind.Should().NotBe(JsonValueKind.Null);
        line.GetProperty("costVarianceApprovedAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task DuplicateLine_WithinOneReceipt_IsRejected()
    {
        Seed seed = await SeedAsync("grnd");
        await factory.CreateUserAsync("grnd-rec", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grnd-app", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string receiver = await SignInAsync(client, "grnd-rec");
        string approver = await SignInAsync(client, "grnd-app");

        Guid orderId = await CreateOrderAsync(client, receiver, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, receiver, approver, orderId);
        Guid lineId = await LineIdAsync(client, receiver, orderId);

        object body = new
        {
            lines = new object[]
            {
                new { purchaseOrderLineId = lineId, quantityReceived = 4m, quantityDamaged = 0m, quantityWrongItem = 0m, quantityExpired = 0m, unitCost = 100m },
                new { purchaseOrderLineId = lineId, quantityReceived = 3m, quantityDamaged = 0m, quantityWrongItem = 0m, quantityExpired = 0m, unitCost = 100m },
            },
        };

        using HttpResponseMessage post = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            body,
            receiver);

        post.StatusCode.Should().Be(HttpStatusCode.BadRequest, await post.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(post)).Should().Be("purchasing.receipt_duplicate_line");
    }

    [Fact]
    public async Task EmptyReceipt_IsRejected()
    {
        Seed seed = await SeedAsync("grne");
        await factory.CreateUserAsync("grne-rec", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grne-app", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string receiver = await SignInAsync(client, "grne-rec");
        string approver = await SignInAsync(client, "grne-app");

        Guid orderId = await CreateOrderAsync(client, receiver, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, receiver, approver, orderId);

        using HttpResponseMessage post = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            new { lines = Array.Empty<object>() },
            receiver);

        post.StatusCode.Should().Be(HttpStatusCode.BadRequest, await post.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(post)).Should().Be("purchasing.receipt_nothing_received");
    }

    [Fact]
    public async Task Cashier_CannotReceive()
    {
        await factory.CreateUserAsync("grnc-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();
        string cashier = await SignInAsync(client, "grnc-cashier");

        using HttpResponseMessage post = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{Guid.CreateVersion7()}/receipts"),
            new { lines = Array.Empty<object>() },
            cashier);

        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExpiredLot_PartiallyRefused_IsRejected_AndFullyRefused_IsOnHoldInQuarantine()
    {
        Seed seed = await SeedAsync("grnx", batchTracked: true, expiryTracked: true);
        await factory.CreateUserAsync("grnx-rec", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grnx-app", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string receiver = await SignInAsync(client, "grnx-rec");
        string approver = await SignInAsync(client, "grnx-app");

        Guid orderId = await CreateOrderAsync(client, receiver, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, receiver, approver, orderId);
        Guid lineId = await LineIdAsync(client, receiver, orderId);

        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        // Refusing only nine of the ten expired units would leak one into stock.
        object partial = new
        {
            lines = new object[]
            {
                new
                {
                    purchaseOrderLineId = lineId,
                    quantityReceived = 10m,
                    quantityDamaged = 0m,
                    quantityWrongItem = 0m,
                    quantityExpired = 9m,
                    unitCost = 100m,
                    lotNumber = "LOT-EXP-1",
                    expiresOn = yesterday,
                },
            },
        };

        using HttpResponseMessage refused = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            partial,
            receiver);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(refused)).Should().Be("purchasing.receipt_expired_on_arrival");

        // Refusing the whole lot posts everything to quarantine.
        object full = new
        {
            lines = new object[]
            {
                new
                {
                    purchaseOrderLineId = lineId,
                    quantityReceived = 10m,
                    quantityDamaged = 0m,
                    quantityWrongItem = 0m,
                    quantityExpired = 10m,
                    unitCost = 100m,
                    lotNumber = "LOT-EXP-1",
                    expiresOn = yesterday,
                },
            },
        };

        using HttpResponseMessage posted = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            full,
            receiver);
        posted.StatusCode.Should().Be(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await posted.Content.ReadAsStringAsync());
        Guid receiptId = created.RootElement.GetProperty("id").GetGuid();

        (HttpStatusCode _, JsonElement detail) = await GetReceiptAsync(client, receiver, orderId, receiptId);
        JsonElement line = detail.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("quantityAccepted").GetDecimal().Should().Be(0m);
        line.GetProperty("lotNumber").GetString().Should().Be("LOT-EXP-1");

        JsonElement expiry = detail.GetProperty("discrepancies").EnumerateArray().Single();
        expiry.GetProperty("kind").GetString().Should().Be("Expired");
        expiry.GetProperty("quantity").GetDecimal().Should().Be(10m);

        List<MovementSnapshot> movements = await MovementSnapshotsAsync(receiptId);
        movements.Single(m => m.State == "Quarantine").QuantityDelta.Should().Be(10m);

        // A batch row was materialised for the lot through the pipeline.
        (await factory.WithServiceAsync(context => context.Batches
            .CountAsync(b => b.LotNumber == "LOT-EXP-1"))).Should().Be(1);
    }

    [Fact]
    public async Task SecondReceipt_AccumulatesToFullyReceived()
    {
        Seed seed = await SeedAsync("grnacc");
        await factory.CreateUserAsync("grnacc-rec", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grnacc-app", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string receiver = await SignInAsync(client, "grnacc-rec");
        string approver = await SignInAsync(client, "grnacc-app");

        Guid orderId = await CreateOrderAsync(client, receiver, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, receiver, approver, orderId);
        Guid lineId = await LineIdAsync(client, receiver, orderId);

        using HttpResponseMessage first = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 6m, unitCost: 100m),
            receiver);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        (HttpStatusCode _, JsonElement afterFirst) = await GetOrderAsync(client, receiver, orderId);
        afterFirst.GetProperty("status").GetString().Should().Be("PartiallyReceived");

        // The second line expects only the remaining four.
        using HttpResponseMessage second = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 4m, unitCost: 100m),
            receiver);
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Guid receiptId = created.RootElement.GetProperty("id").GetGuid();

        (HttpStatusCode _, JsonElement detail) = await GetReceiptAsync(client, receiver, orderId, receiptId);
        JsonElement line = detail.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("quantityExpected").GetDecimal().Should().Be(4m);
        line.GetProperty("quantityReceived").GetDecimal().Should().Be(4m);
        line.GetProperty("quantityAccepted").GetDecimal().Should().Be(4m);
        line.GetProperty("acceptedState").GetString().Should().Be("PendingInspection");
        detail.GetProperty("discrepancies").GetArrayLength().Should().Be(0);

        (HttpStatusCode _, JsonElement completed) = await GetOrderAsync(client, receiver, orderId);
        completed.GetProperty("status").GetString().Should().Be("FullyReceived");
    }

    [Fact]
    public async Task StoreManager_ReceivesAtTheirOwnStore_ThroughTheScopedPath()
    {
        Seed seed = await SeedAsync("grnscope");
        await factory.CreateUserAsync("grnscope-manager", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("grnscope-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        // A store manager holds purchase.receive and inventory.receive but never
        // location.all: every check must resolve through the location assignment.
        await factory.CreateUserAsync("grnscope-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "grnscope-manager");
        string approver = await SignInAsync(client, "grnscope-approver");
        string store = await SignInAsync(client, "grnscope-store");

        Guid orderId = await CreateOrderAsync(client, manager, seed, quantity: 10m, unitCost: 100m);
        await SendOrderAsync(client, manager, approver, orderId);
        Guid lineId = await LineIdAsync(client, store, orderId);

        using HttpResponseMessage post = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId}/receipts"),
            ReceiptBody(lineId, received: 10m, unitCost: 100m),
            store);
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        using JsonDocument created = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Guid receiptId = created.RootElement.GetProperty("id").GetGuid();

        (HttpStatusCode _, JsonElement detail) = await GetReceiptAsync(client, store, orderId, receiptId);
        detail.GetProperty("status").GetString().Should().Be("Posted");
        detail.GetProperty("destinationLocationId").GetGuid().Should().Be(seed.Store.Value);
    }

    private async Task<Seed> SeedAsync(string suffix, bool batchTracked = false, bool expiryTracked = false)
    {
        SupplierId supplier = await factory.CreateSupplierAsync($"SUP-{suffix}", $"Supplier {suffix}");
        LocationId store = await factory.CreateLocationAsync($"PO-{suffix}", $"Store {suffix}");
        string barcode = Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture);
        ProductId product = await factory.CreateProductAsync($"PO-{suffix}", $"Product {suffix}", barcode, batchTracked, expiryTracked);
        UnitOfMeasureId baseUnit = await factory.CreateUnitOfMeasureAsync("PC", "Piece");

        return new Seed(supplier, store, product, baseUnit);
    }

    private static object ReceiptBody(Guid purchaseOrderLineId, decimal received, decimal unitCost)
        => new
        {
            lines = new object[]
            {
                new
                {
                    purchaseOrderLineId,
                    quantityReceived = received,
                    quantityDamaged = 0m,
                    quantityWrongItem = 0m,
                    quantityExpired = 0m,
                    unitCost,
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

    private async Task<int> ReceiptCountAsync(Guid orderId)
        => await factory.WithServiceAsync(context => context.GoodsReceipts
            .CountAsync(r => r.PurchaseOrderId == new PurchaseOrderId(orderId)));

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("errorCode", out JsonElement code)
            ? code.GetString()
            : null;
    }

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