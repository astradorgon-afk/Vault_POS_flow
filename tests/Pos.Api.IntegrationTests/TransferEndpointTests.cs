using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Transfers;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The transfer lifecycle exercised through the real pipeline: request through
/// review and approval, FEFO picking, dispatch, arrival with its discrepancies
/// and resolutions, cancellation, and the ledger groups each step posts.
/// </summary>
[Collection("api")]
public sealed class TransferEndpointTests(PosApiFactory factory)
{
    // Product barcodes must be unique within the shared database. A process-wide
    // counter is the simplest way to guarantee that even if collections ever run
    // in parallel.
    private static long _barcodeSequence = 7400000000000;

    [Fact]
    public async Task Transfer_FullLifecycle_PostsBalancedLedger_AndCloses()
    {
        Seed seed = await SeedAsync("lcf");
        await factory.CreateUserAsync("lcf-requester", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("lcf-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string requester = await SignInAsync(client, "lcf-requester");
        string approver = await SignInAsync(client, "lcf-approver");

        await SeedAvailableAsync(seed.Warehouse, seed.Product, batchId: null, quantity: 10m, unitCost: 95m);

        Guid transferId = await CreateTransferAsync(client, requester, seed, quantity: 6m);

        TransferDbo draft = await TransferDboAsync(transferId);
        draft.Status.Should().Be(TransferStatus.Draft);
        draft.Number.Should().BeEmpty();
        draft.ShipmentId.Should().BeNull();
        draft.ReceiptId.Should().BeNull();

        await RequestThroughApprovalAsync(client, requester, approver, transferId);

        using HttpResponseMessage picked = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/pick"),
            new
            {
                allocations = new object[]
                {
                    new { lineNo = 1, batchId = (Guid?)null, quantity = 6m },
                },
            },
            approver);
        picked.StatusCode.Should().Be(HttpStatusCode.OK, await picked.Content.ReadAsStringAsync());

        using HttpResponseMessage ready = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/ready"), approver);
        ready.StatusCode.Should().Be(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());

        using HttpResponseMessage dispatched = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/dispatch"), approver);
        dispatched.StatusCode.Should().Be(HttpStatusCode.OK, await dispatched.Content.ReadAsStringAsync());

        TransferDbo afterDispatch = await TransferDboAsync(transferId);
        afterDispatch.Status.Should().Be(TransferStatus.Dispatched);
        afterDispatch.Number.Should().MatchRegex(@"^TRF-\d{4}-\d{6}$");
        afterDispatch.ShipmentNumber.Should().MatchRegex(@"^SHP-\d{4}-\d{6}$");
        afterDispatch.ShipmentId.Should().NotBeNull();

        // Dispatch pulls six from available into in-transit at the source,
        // stamped with the shipment number.
        List<MovementSnapshot> dispatchMovements = await MovementsByReferenceAsync(afterDispatch.ShipmentId!.Value);
        dispatchMovements.Should().HaveCount(2);
        dispatchMovements.Sum(m => m.QuantityDelta).Should().Be(0m);
        dispatchMovements.Single(m => m.State == InventoryState.Available).QuantityDelta.Should().Be(-6m);
        dispatchMovements.Single(m => m.State == InventoryState.InTransit).QuantityDelta.Should().Be(6m);
        dispatchMovements.Should().OnlyContain(m =>
            m.MovementType == InventoryMovementType.TransferDispatch
            && Regex.IsMatch(m.ReferenceNumber, @"^SHP-\d{4}-\d{6}$"));

        (await BalanceAsync(seed.Warehouse, seed.Product, null, InventoryState.Available)).Should().Be(4m);
        (await BalanceAsync(seed.Warehouse, seed.Product, null, InventoryState.InTransit)).Should().Be(6m);

        using HttpResponseMessage received = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/receive"),
            new
            {
                receives = new object[]
                {
                    new { lineNo = 1, batchId = (Guid?)null, receivedQuantity = 6m, damagedQuantity = 0m },
                },
            },
            approver);
        received.StatusCode.Should().Be(HttpStatusCode.OK, await received.Content.ReadAsStringAsync());

        TransferDbo afterReceive = await TransferDboAsync(transferId);
        afterReceive.Status.Should().Be(TransferStatus.Received);
        afterReceive.ReceiptNumber.Should().MatchRegex(@"^TRC-\d{4}-\d{6}$");
        afterReceive.ReceiptId.Should().NotBeNull();

        List<MovementSnapshot> receiveMovements = await MovementsByReferenceAsync(afterReceive.ReceiptId!.Value);
        receiveMovements.Should().HaveCount(2);
        receiveMovements.Sum(m => m.QuantityDelta).Should().Be(0m);
        receiveMovements.Single(m => m.State == InventoryState.InTransit).QuantityDelta.Should().Be(-6m);
        receiveMovements.Single(m => m.State == InventoryState.Available).QuantityDelta.Should().Be(6m);
        receiveMovements.Should().OnlyContain(m => m.MovementType == InventoryMovementType.TransferReceipt);

        // The store now holds six at the snapshot cost of the dispatched stock.
        BalanceSnapshot storeAvailable = await BalanceSnapshotAsync(seed.Store, seed.Product, null, InventoryState.Available);
        storeAvailable.Quantity.Should().Be(6m);
        storeAvailable.AverageUnitCost.Should().Be(95m);
        (await BalanceAsync(seed.Warehouse, seed.Product, null, InventoryState.InTransit)).Should().Be(0m);

        using HttpResponseMessage verified = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/verify"), approver);
        verified.StatusCode.Should().Be(HttpStatusCode.OK, await verified.Content.ReadAsStringAsync());

        TransferDbo closed = await TransferDboAsync(transferId);
        closed.Status.Should().Be(TransferStatus.Closed);
        closed.CustodyKinds.Should().Equal(
            "Created", "Submitted", "Reviewed", "Approved", "Picked", "Dispatched", "Received", "Verified");
    }

    [Fact]
    public async Task Transfer_Fefo_ForcesEarlierExpiryFirst()
    {
        Seed seed = await SeedAsync("fefo");
        await factory.CreateUserAsync("fefo-requester", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("fefo-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string requester = await SignInAsync(client, "fefo-requester");
        string approver = await SignInAsync(client, "fefo-approver");

        // The shared seed product is batch-blind; a lot-tracked product is what
        // exercises expiry-aware picking.
        string barcode = Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture);
        ProductId batched = await factory.CreateProductAsync(
            $"FB-{seed.Suffix}", $"Batched {seed.Suffix}", barcode,
            tracksBatches: true, tracksExpiry: true);
        Seed batchedSeed = seed with { Product = batched };

        // Two available lots of the same batch-tracked product: the earlier one
        // expires first and must be fully exhausted before the later is touched.
        DateOnly earlier = new(2026, 11, 1);
        DateOnly later = new(2027, 1, 1);
        BatchId batchEarlier = await SeedBatchAsync(batchedSeed, "LOT-E", expiresOn: earlier, unitCost: 100m, quantity: 5m);
        BatchId batchLater = await SeedBatchAsync(batchedSeed, "LOT-L", expiresOn: later, unitCost: 120m, quantity: 5m);

        Guid transferId = await CreateTransferAsync(client, requester, batchedSeed, quantity: 6m);
        await RequestThroughApprovalAsync(client, requester, approver, transferId);

        // Picking the later lot alone skips the earlier-expiring stock: one
        // unit is available there, but FEFO demands the earlier lot first.
        using HttpResponseMessage skipped = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/pick"),
            new
            {
                allocations = new object[]
                {
                    new { lineNo = 1, batchId = batchLater.Value, quantity = 1m },
                },
            },
            approver);
        skipped.StatusCode.Should().Be(HttpStatusCode.BadRequest, await skipped.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(skipped)).Should().Be("transfer.pick_skips_earlier_expiry");

        // The canonical FEFO prefix is five from the earlier lot, one from the
        // later one, each carrying its own snapshot cost.
        using HttpResponseMessage picked = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/pick"),
            new
            {
                allocations = new object[]
                {
                    new { lineNo = 1, batchId = batchEarlier.Value, quantity = 5m },
                    new { lineNo = 1, batchId = batchLater.Value, quantity = 1m },
                },
            },
            approver);
        picked.StatusCode.Should().Be(HttpStatusCode.OK, await picked.Content.ReadAsStringAsync());

        using HttpResponseMessage ready = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/ready"), approver);
        ready.StatusCode.Should().Be(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());

        using HttpResponseMessage dispatched = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/dispatch"), approver);
        dispatched.StatusCode.Should().Be(HttpStatusCode.OK, await dispatched.Content.ReadAsStringAsync());

        Guid shipmentId = (await TransferDboAsync(transferId)).ShipmentId!.Value;

        List<MovementSnapshot> dispatchMovements = await MovementsByReferenceAsync(shipmentId);
        dispatchMovements.Should().HaveCount(4);
        dispatchMovements.Sum(m => m.QuantityDelta).Should().Be(0m);
        dispatchMovements.Single(m => m.State == InventoryState.Available && m.BatchId == batchEarlier.Value)
            .QuantityDelta.Should().Be(-5m);
        dispatchMovements.Single(m => m.State == InventoryState.Available && m.BatchId == batchLater.Value)
            .QuantityDelta.Should().Be(-1m);
        dispatchMovements.Single(m => m.State == InventoryState.InTransit && m.BatchId == batchEarlier.Value)
            .QuantityDelta.Should().Be(5m);
        dispatchMovements.Single(m => m.State == InventoryState.InTransit && m.BatchId == batchLater.Value)
            .QuantityDelta.Should().Be(1m);

        using HttpResponseMessage received = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/receive"),
            new
            {
                receives = new object[]
                {
                    new { lineNo = 1, batchId = batchEarlier.Value, receivedQuantity = 5m, damagedQuantity = 0m },
                    new { lineNo = 1, batchId = batchLater.Value, receivedQuantity = 1m, damagedQuantity = 0m },
                },
            },
            approver);
        received.StatusCode.Should().Be(HttpStatusCode.OK, await received.Content.ReadAsStringAsync());

        using HttpResponseMessage verified = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/verify"), approver);
        verified.StatusCode.Should().Be(HttpStatusCode.OK, await verified.Content.ReadAsStringAsync());

        (await TransferDboAsync(transferId)).Status.Should().Be(TransferStatus.Closed);

        // Each lot reached the store with its own cost intact.
        string dump = await DumpStoreBucketsAsync(seed.Store, batched);
        BalanceSnapshot earlierAtStore =
            await BalanceSnapshotAsync(seed.Store, batched, batchEarlier, InventoryState.Available);
        earlierAtStore.Quantity.Should().Be(5m, dump);
        earlierAtStore.AverageUnitCost.Should().Be(100m, dump);

        BalanceSnapshot laterAtStore =
            await BalanceSnapshotAsync(seed.Store, batched, batchLater, InventoryState.Available);
        laterAtStore.Quantity.Should().Be(1m, dump);
        laterAtStore.AverageUnitCost.Should().Be(120m, dump);
    }

    [Fact]
    public async Task Transfer_PartialArrival_ResolvedFound_ReturnsStockToAvailable()
    {
        Seed seed = await SeedAsync("fnd");
        await factory.CreateUserAsync("fnd-requester", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("fnd-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string requester = await SignInAsync(client, "fnd-requester");
        string approver = await SignInAsync(client, "fnd-approver");

        await SeedAvailableAsync(seed.Warehouse, seed.Product, batchId: null, quantity: 10m, unitCost: 95m);

        Guid transferId = await CreateTransferAsync(client, requester, seed, quantity: 6m);
        await RequestThroughApprovalAsync(client, requester, approver, transferId);
        await PickReadyDispatchAsync(client, approver, transferId, [(null, 6m)]);

        // Four of six arrive: two sit as an unresolved variance at the store.
        using HttpResponseMessage received = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/receive"),
            new
            {
                receives = new object[]
                {
                    new { lineNo = 1, batchId = (Guid?)null, receivedQuantity = 4m, damagedQuantity = 0m },
                },
            },
            approver);
        received.StatusCode.Should().Be(HttpStatusCode.OK, await received.Content.ReadAsStringAsync());

        TransferDbo partial = await TransferDboAsync(transferId);
        partial.Status.Should().Be(TransferStatus.PartiallyReceived);
        partial.DiscrepancyQuantities.Should().Equal(2m);

        Guid receiptId = partial.ReceiptId!.Value;
        List<MovementSnapshot> arrival = await MovementsByReferenceAsync(receiptId);
        arrival.Should().HaveCount(3);
        arrival.Sum(m => m.QuantityDelta).Should().Be(0m);
        arrival.Single(m => m.State == InventoryState.InTransit).QuantityDelta.Should().Be(-6m);
        arrival.Single(m => m.State == InventoryState.Available).QuantityDelta.Should().Be(4m);
        arrival.Single(m => m.State == InventoryState.TransitVariance).QuantityDelta.Should().Be(2m);

        // The variance blocks verification until it is resolved.
        using HttpResponseMessage blocked = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/verify"), approver);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict, await blocked.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(blocked)).Should().Be("transfer.verify_has_open_variance");

        // The open shortage is what the discrepancy alert worker reads.
        TransferShortageAlert shortage = (await OpenShortagesAsync())
            .Should().ContainSingle(a => a.TransferId == new TransferOrderId(transferId)).Subject;
        shortage.ShortQuantity.Should().Be(2m);
        shortage.ShortLineCount.Should().Be(1);
        shortage.DestinationLocationId.Should().Be(seed.Store);

        using HttpResponseMessage resolved = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/discrepancies/{partial.DiscrepancyIds[0]}/resolve"),
            new { outcome = 1, note = "Found in the back room." },
            approver);
        resolved.StatusCode.Should().Be(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());
        (await OpenShortagesAsync()).Should().NotContain(a => a.TransferId == new TransferOrderId(transferId));

        // The resolution is balanced: variance leaves the destination and the
        // found units re-enter available there.
        string transferNumber = (await TransferDboAsync(transferId)).Number;
        List<MovementSnapshot> resolution = await MovementsByReferenceAsync(transferId);
        resolution.Should().HaveCount(2);
        resolution.Sum(m => m.QuantityDelta).Should().Be(0m);
        resolution.Single(m => m.State == InventoryState.TransitVariance).QuantityDelta.Should().Be(-2m);
        resolution.Single(m => m.State == InventoryState.Available).QuantityDelta.Should().Be(2m);
        resolution.Should().OnlyContain(m =>
            m.MovementType == InventoryMovementType.TransitVarianceResolveFound
            && m.ReferenceNumber == transferNumber);

        using HttpResponseMessage verified = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/verify"), approver);
        verified.StatusCode.Should().Be(HttpStatusCode.OK, await verified.Content.ReadAsStringAsync());

        (await TransferDboAsync(transferId)).Status.Should().Be(TransferStatus.Closed);
        (await BalanceAsync(seed.Store, seed.Product, null, InventoryState.Available)).Should().Be(6m);
        (await BalanceAsync(seed.Store, seed.Product, null, InventoryState.TransitVariance)).Should().Be(0m);
    }

    [Fact]
    public async Task Transfer_WriteOff_PostsToTheExternalCounterparty()
    {
        Seed seed = await SeedAsync("woff");
        await factory.CreateUserAsync("woff-requester", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("woff-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);
        using HttpClient client = factory.CreateClient();
        string requester = await SignInAsync(client, "woff-requester");
        string approver = await SignInAsync(client, "woff-approver");

        await SeedAvailableAsync(seed.Warehouse, seed.Product, batchId: null, quantity: 10m, unitCost: 95m);

        Guid transferId = await CreateTransferAsync(client, requester, seed, quantity: 6m);
        await RequestThroughApprovalAsync(client, requester, approver, transferId);
        await PickReadyDispatchAsync(client, approver, transferId, [(null, 6m)]);

        using HttpResponseMessage received = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/receive"),
            new
            {
                receives = new object[]
                {
                    new { lineNo = 1, batchId = (Guid?)null, receivedQuantity = 5m, damagedQuantity = 0m },
                },
            },
            approver);
        received.StatusCode.Should().Be(HttpStatusCode.OK, await received.Content.ReadAsStringAsync());

        TransferDbo partial = await TransferDboAsync(transferId);
        partial.Status.Should().Be(TransferStatus.PartiallyReceived);

        using HttpResponseMessage resolved = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/discrepancies/{partial.DiscrepancyIds[0]}/resolve"),
            new { outcome = 2, note = "Lost in transit." },
            approver);
        resolved.StatusCode.Should().Be(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());

        // The write-off posts the missing units to the external counterparty:
        // the variance bucket is zeroed and EXT-WRITEOFF gains the short qty.
        List<MovementSnapshot> resolution = await MovementsByReferenceAsync(transferId);
        resolution.Should().HaveCount(2);
        resolution.Sum(m => m.QuantityDelta).Should().Be(0m);
        resolution.Single(m => m.State == InventoryState.TransitVariance).QuantityDelta.Should().Be(-1m);
        MovementSnapshot externalLeg = resolution.Single(m => m.State == InventoryState.External);
        externalLeg.QuantityDelta.Should().Be(1m);
        externalLeg.LocationId.Should().NotBe(seed.Store.Value);
        resolution.Should().OnlyContain(m => m.MovementType == InventoryMovementType.TransitVarianceWriteOff);

        using HttpResponseMessage verified = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/verify"), approver);
        verified.StatusCode.Should().Be(HttpStatusCode.OK, await verified.Content.ReadAsStringAsync());

        (await TransferDboAsync(transferId)).Status.Should().Be(TransferStatus.Closed);
        (await BalanceAsync(seed.Store, seed.Product, null, InventoryState.TransitVariance)).Should().Be(0m);
    }

    [Fact]
    public async Task Transfer_CancelDispatch_ReversesTheLedger_AndRequiresReasonAndApproval()
    {
        Seed seed = await SeedAsync("canc");
        await factory.CreateUserAsync("canc-requester", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("canc-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        // A store manager may dispatch but never cancel a dispatch: that reversal
        // needs transfer.approve, which this role does not hold.
        await factory.CreateUserAsync("canc-store", Roles.StoreManager, locations: [seed.Store]);
        using HttpClient client = factory.CreateClient();
        string requester = await SignInAsync(client, "canc-requester");
        string approver = await SignInAsync(client, "canc-approver");
        string store = await SignInAsync(client, "canc-store");

        await SeedAvailableAsync(seed.Warehouse, seed.Product, batchId: null, quantity: 10m, unitCost: 95m);

        Guid transferId = await CreateTransferAsync(client, requester, seed, quantity: 6m);
        await RequestThroughApprovalAsync(client, requester, approver, transferId);
        await PickReadyDispatchAsync(client, approver, transferId, [(null, 6m)]);

        TransferDbo dispatched = await TransferDboAsync(transferId);
        Guid shipmentId = dispatched.ShipmentId!.Value;

        // A blank reason reaches the aggregate and refuses the cancellation.
        using HttpResponseMessage noReason = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/cancel-dispatch"),
            new { reason = "" },
            approver);
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest, await noReason.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(noReason)).Should().Be("transfer.cancel_reason_required");

        // The handler refuses a dispatcher without the approve permission.
        using HttpResponseMessage notApprover = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/cancel-dispatch"),
            new { reason = "Found the stock; do not ship." },
            store);
        notApprover.StatusCode.Should().Be(HttpStatusCode.Forbidden, await notApprover.Content.ReadAsStringAsync());

        using HttpResponseMessage cancelled = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/cancel-dispatch"),
            new { reason = "Found in staging; shipment not needed." },
            approver);
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());

        TransferDbo afterCancel = await TransferDboAsync(transferId);
        afterCancel.Status.Should().Be(TransferStatus.Cancelled);
        afterCancel.CustodyKinds.Should().EndWith("DispatchCancelled");

        // Both the dispatch and the cancellation post against the shipment: four
        // zero-sum movements in total, with the stock restored to available.
        List<MovementSnapshot> movements = await MovementsByReferenceAsync(shipmentId);
        movements.Should().HaveCount(4);
        movements.Sum(m => m.QuantityDelta).Should().Be(0m);
        movements.Where(m => m.MovementType == InventoryMovementType.TransferDispatch)
            .Single(m => m.State == InventoryState.Available).QuantityDelta.Should().Be(-6m);
        movements.Where(m => m.MovementType == InventoryMovementType.TransferCancelDispatch)
            .Single(m => m.State == InventoryState.InTransit).QuantityDelta.Should().Be(-6m);
        movements.Where(m => m.MovementType == InventoryMovementType.TransferCancelDispatch)
            .Single(m => m.State == InventoryState.Available).QuantityDelta.Should().Be(6m);

        (await BalanceAsync(seed.Warehouse, seed.Product, null, InventoryState.Available)).Should().Be(10m);
        (await BalanceAsync(seed.Warehouse, seed.Product, null, InventoryState.InTransit)).Should().Be(0m);
    }

    [Fact]
    public async Task Cashier_CannotRequestATransfer()
    {
        await factory.CreateUserAsync("trfc-cashier", Roles.Cashier);
        using HttpClient client = factory.CreateClient();
        string cashier = await SignInAsync(client, "trfc-cashier");

        using HttpResponseMessage created = await PostAsJsonAsync(
            client,
            "/api/v1/transfers",
            new
            {
                sourceLocationId = Guid.Empty,
                destinationLocationId = Guid.Empty,
                lines = new object[]
                {
                    new { productId = Guid.Empty, quantity = 1m, note = (string?)null },
                },
            },
            cashier);

        created.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StoreManager_List_IsScopedToAssignedLocations()
    {
        Seed seed = await SeedAsync("scop");
        await factory.CreateUserAsync(
            "scop-manager", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited, locations: [seed.Warehouse]);
        await factory.CreateUserAsync("scop-store", Roles.StoreManager, locations: [seed.Store]);
        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "scop-manager");
        string store = await SignInAsync(client, "scop-store");

        // A warehouse-to-warehouse transfer touches none of the store manager's
        // locations, so it must stay invisible to them.
        LocationId secondWarehouse =
            await factory.CreateLocationAsync($"WH2-{seed.Suffix}", $"Warehouse 2 {seed.Suffix}", LocationKind.MainWarehouse);

        using HttpResponseMessage created = await PostAsJsonAsync(
            client,
            "/api/v1/transfers",
            new
            {
                sourceLocationId = seed.Warehouse.Value,
                destinationLocationId = secondWarehouse.Value,
                lines = new object[]
                {
                    new { productId = seed.Product.Value, quantity = 2m },
                },
            },
            manager);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        using JsonDocument createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid transferId = createdBody.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage storeList = await GetAsync(client, "/api/v1/transfers/", store);
        storeList.StatusCode.Should().Be(HttpStatusCode.OK);
        using (JsonDocument document = JsonDocument.Parse(await storeList.Content.ReadAsStringAsync()))
        {
            document.RootElement.GetArrayLength().Should().Be(0);
        }

        using HttpResponseMessage managerList = await GetAsync(client, "/api/v1/transfers/", manager);
        managerList.StatusCode.Should().Be(HttpStatusCode.OK);
        using (JsonDocument document = JsonDocument.Parse(await managerList.Content.ReadAsStringAsync()))
        {
            JsonElement summary = document.RootElement.EnumerateArray()
                .Single(x => x.GetProperty("id").GetGuid() == transferId);
            summary.GetProperty("status").GetString().Should().Be("Draft");
            summary.GetProperty("sourceLocationId").GetGuid().Should().Be(seed.Warehouse.Value);
            summary.GetProperty("destinationLocationId").GetGuid().Should().Be(secondWarehouse.Value);
        }
    }

    [Fact]
    public async Task ResolveDiscrepancy_WithoutReconcilePermission_IsForbidden()
    {
        Seed seed = await SeedAsync("rslf");
        await factory.CreateUserAsync("rslf-requester", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("rslf-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("rslf-store", Roles.StoreManager, locations: [seed.Store]);
        using HttpClient client = factory.CreateClient();
        string requester = await SignInAsync(client, "rslf-requester");
        string approver = await SignInAsync(client, "rslf-approver");
        string store = await SignInAsync(client, "rslf-store");

        await SeedAvailableAsync(seed.Warehouse, seed.Product, batchId: null, quantity: 10m, unitCost: 95m);

        Guid transferId = await CreateTransferAsync(client, requester, seed, quantity: 6m);
        await RequestThroughApprovalAsync(client, requester, approver, transferId);
        await PickReadyDispatchAsync(client, approver, transferId, [(null, 6m)]);

        using HttpResponseMessage received = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/receive"),
            new
            {
                receives = new object[]
                {
                    new { lineNo = 1, batchId = (Guid?)null, receivedQuantity = 5m, damagedQuantity = 0m },
                },
            },
            approver);
        received.StatusCode.Should().Be(HttpStatusCode.OK, await received.Content.ReadAsStringAsync());

        // The route permission check refuses the store manager before any
        // handler logic runs.
        using HttpResponseMessage resolved = await PostAsJsonAsync(
            client,
            "/api/v1/transfers/00000000-0000-0000-0000-000000000000/discrepancies/00000000-0000-0000-0000-000000000000/resolve",
            new { outcome = 1 },
            store);

        resolved.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<Seed> SeedAsync(string suffix)
    {
        SupplierId supplier = await factory.CreateSupplierAsync($"SUP-{suffix}", $"Supplier {suffix}");
        LocationId warehouse = await factory.CreateLocationAsync($"WH-{suffix}", $"Warehouse {suffix}", LocationKind.MainWarehouse);
        LocationId store = await factory.CreateLocationAsync($"ST-{suffix}", $"Store {suffix}", LocationKind.Store);
        string barcode = Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture);
        ProductId product = await factory.CreateProductAsync($"TR-{suffix}", $"Product {suffix}", barcode, defaultPurchaseCost: 95m);

        return new Seed(supplier, warehouse, store, product, suffix);
    }

    /// <summary>
    /// Stocks a bucket directly. No production flow produces available stock yet
    /// (goods receipts arrive in pending inspection), so the tests seed the
    /// balance projection the ledger would have produced.
    /// </summary>
    private Task<int> SeedAvailableAsync(
        LocationId locationId, ProductId productId, BatchId? batchId, decimal quantity, decimal unitCost)
        => factory.WithServiceAsync(async context =>
        {
            Guid batchKey = batchId?.Value ?? Guid.Empty;
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

    private async Task<BatchId> SeedBatchAsync(
        Seed seed, string lot, DateOnly? expiresOn, decimal unitCost, decimal quantity)
    {
        Result<Batch> created = Batch.Create(
            seed.Product,
            seed.Supplier,
            lot,
            receivedOn: DateOnly.FromDateTime(DateTime.UtcNow),
            manufacturedOn: null,
            expiresOn,
            unitCost: unitCost,
            createdByUserId: new UserId(Guid.CreateVersion7()),
            now: DateTimeOffset.UtcNow);

        created.IsSuccess.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));

        await factory.WithServiceAsync(async context =>
        {
            context.Batches.Add(created.Value);
            await context.SaveChangesAsync();
            return 0;
        });

        await SeedAvailableAsync(seed.Warehouse, seed.Product, created.Value.Id, quantity, unitCost);
        return created.Value.Id;
    }

    private static object TransferBody(Seed seed, decimal quantity)
        => new
        {
            sourceLocationId = seed.Warehouse.Value,
            destinationLocationId = seed.Store.Value,
            lines = new object[]
            {
                new { productId = seed.Product.Value, quantity },
            },
        };

    private static async Task<Guid> CreateTransferAsync(
        HttpClient client, string accessToken, Seed seed, decimal quantity)
    {
        using HttpResponseMessage response = await PostAsJsonAsync(
            client, "/api/v1/transfers", TransferBody(seed, quantity), accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task RequestThroughApprovalAsync(
        HttpClient client, string requester, string approver, Guid transferId)
    {
        using HttpResponseMessage submitted = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/submit"), requester);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());

        using HttpResponseMessage reviewed = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/review"),
            new { note = "Reviewing the request." },
            approver);
        reviewed.StatusCode.Should().Be(HttpStatusCode.OK, await reviewed.Content.ReadAsStringAsync());

        using HttpResponseMessage approved = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/approve"),
            new { note = "Approved." },
            approver);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
    }

    private static async Task PickReadyDispatchAsync(
        HttpClient client, string accessToken, Guid transferId, IReadOnlyList<(Guid? BatchId, decimal Quantity)> allocations)
    {
        using HttpResponseMessage picked = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/pick"),
            new
            {
                allocations = allocations
                    .Select(a => new { lineNo = 1, batchId = a.BatchId, quantity = a.Quantity })
                    .ToArray(),
            },
            accessToken);
        picked.StatusCode.Should().Be(HttpStatusCode.OK, await picked.Content.ReadAsStringAsync());

        using HttpResponseMessage ready = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/ready"), accessToken);
        ready.StatusCode.Should().Be(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());

        using HttpResponseMessage dispatched = await PostAsync(
            client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/dispatch"), accessToken);
        dispatched.StatusCode.Should().Be(HttpStatusCode.OK, await dispatched.Content.ReadAsStringAsync());
    }

    private Task<IReadOnlyList<TransferShortageAlert>> OpenShortagesAsync()
        => factory.WithServiceAsync(context => new DiscrepancyAlertRepository(context)
            .GetOpenTransferShortagesAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));

    private async Task<TransferDbo> TransferDboAsync(Guid transferId)
        => await factory.WithServiceAsync(async context =>
        {
            Transfer? transfer = await context.Transfers
                .AsNoTracking()
                .Include(t => t.Discrepancies)
                .Include(t => t.CustodyEvents)
                .FirstOrDefaultAsync(t => t.Id == new TransferOrderId(transferId));

            transfer.Should().NotBeNull();

            return new TransferDbo(
                transfer!.Status,
                transfer.Number,
                transfer.ShipmentId?.Value,
                transfer.ShipmentNumber,
                transfer.ReceiptId?.Value,
                transfer.ReceiptNumber,
                [.. transfer.Discrepancies.Select(d => d.Id.Value)],
                [.. transfer.Discrepancies.Select(d => d.Quantity)],
                [.. transfer.CustodyEvents.OrderBy(e => e.Sequence).Select(e => e.Kind.ToString())]);
        });

    private async Task<List<MovementSnapshot>> MovementsByReferenceAsync(Guid referenceDocumentId)
        => await factory.WithServiceAsync(context => context.InventoryMovements
            .Where(m => m.ReferenceDocumentId == referenceDocumentId)
            .OrderBy(m => m.QuantityDelta)
            .Select(m => new MovementSnapshot(
                m.QuantityDelta,
                m.State,
                m.MovementType,
                m.ReferenceNumber,
                m.LocationId.Value,
                m.BatchKey.Value))
            .ToListAsync());

    private async Task<decimal> BalanceAsync(
        LocationId locationId, ProductId productId, BatchId? batchId, InventoryState state)
        => (await BalanceSnapshotAsync(locationId, productId, batchId, state)).Quantity;

    private async Task<BalanceSnapshot> BalanceSnapshotAsync(
        LocationId locationId, ProductId productId, BatchId? batchId, InventoryState state)
        => await factory.WithServiceAsync(async context =>
        {
            InventoryBalance? balance = await context.InventoryBalances
                .AsNoTracking()
                .FirstOrDefaultAsync(b =>
                    b.LocationId == locationId
                    && b.ProductId == productId
                    && b.BatchKey == (batchId ?? BatchId.Empty)
                    && b.State == state);

            return balance is null
                ? new BalanceSnapshot(0m, 0m)
                : new BalanceSnapshot(balance.Quantity, balance.AverageUnitCost);
        });

    private async Task<string> DumpStoreBucketsAsync(LocationId locationId, ProductId productId)
        => await factory.WithServiceAsync(async context =>
        {
            List<InventoryBalance> rows = await context.InventoryBalances
                .AsNoTracking()
                .Where(b => b.LocationId == locationId && b.ProductId == productId)
                .OrderBy(b => b.State)
                .ThenBy(b => b.BatchKey)
                .ToListAsync();

            return rows.Count == 0
                ? "no balance rows"
                : string.Join(" | ", rows.Select(r => $"state={r.State} batch={r.BatchKey} qty={r.Quantity} cost={r.AverageUnitCost}"));
        });

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

    private sealed record MovementSnapshot(
        decimal QuantityDelta,
        InventoryState State,
        InventoryMovementType MovementType,
        string ReferenceNumber,
        Guid LocationId,
        Guid BatchId);

    private sealed record TransferDbo(
        TransferStatus Status,
        string Number,
        Guid? ShipmentId,
        string? ShipmentNumber,
        Guid? ReceiptId,
        string? ReceiptNumber,
        IReadOnlyList<Guid> DiscrepancyIds,
        IReadOnlyList<decimal> DiscrepancyQuantities,
        IReadOnlyList<string> CustodyKinds);

    private sealed record BalanceSnapshot(decimal Quantity, decimal AverageUnitCost);

    private sealed record Seed(
        SupplierId Supplier,
        LocationId Warehouse,
        LocationId Store,
        ProductId Product,
        string Suffix);
}
