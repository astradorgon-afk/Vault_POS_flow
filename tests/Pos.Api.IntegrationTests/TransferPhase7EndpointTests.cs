using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Application.Notifications;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Transfers;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Phase 7 store-to-store: emergency offline transfers with dual-manager
/// co-signature and central review, single-use pre-approval tokens, and
/// replenishment recommendations — all through the real pipeline.
/// </summary>
[Collection("api")]
public sealed class TransferPhase7EndpointTests(PosApiFactory factory)
{
    // Barcodes are unique per test class to keep the shared database free of
    // collisions (7500... sits outside the ranges the other classes use).
    private static long _barcodeSequence = 7500000000000;

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EmergencyTransfer_CoSigns_PostsLedgerImmediately_AndRatifies()
    {
        Seed seed = await SeedAsync("emg1");
        await factory.CreateUserAsync("emg1-a", Roles.MainInventoryManager, locations: [seed.StoreA], tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("emg1-b", Roles.MainInventoryManager, locations: [seed.StoreB], tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("emg1-store", Roles.StoreManager, locations: [seed.StoreA]);
        using HttpClient client = factory.CreateClient();
        string bearer = await SignInAsync(client, "emg1-a");
        string coSigner = await SignInAsync(client, "emg1-b");
        string storeManager = await SignInAsync(client, "emg1-store");

        await SeedAvailableAsync(seed.StoreA, seed.Product, batchId: null, quantity: 10m, unitCost: 95m);

        Guid transferId = await InitiateEmergencyAsync(
            client, bearer, seed, quantity: 6m, coSignerUserName: "emg1-b");

        TransferDbo emergency = await TransferDboAsync(transferId);
        emergency.Status.Should().Be(TransferStatus.PendingCentralReview);
        emergency.Mode.Should().Be(TransferMode.EmergencyOffline);
        emergency.Number.Should().MatchRegex(@"^TRF-\d{4}-\d{6}$");
        emergency.CustodyKinds.Should().Equal("Created", "EmergencyCreated");

        IReadOnlyList<EmergencyTransferAlert> recentAlerts = await factory.WithServiceAsync(context =>
            new EmergencyTransferAlertRepository(context)
                .GetRecentAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));
        EmergencyTransferAlert alert = recentAlerts.Single(a => a.TransferId.Value == transferId);
        alert.TransferNumber.Should().Be(emergency.Number);
        alert.SourceLocationId.Should().Be(seed.StoreA);
        alert.DestinationLocationId.Should().Be(seed.StoreB);
        alert.LineCount.Should().Be(1);
        alert.TotalQuantity.Should().Be(6m);

        // The stock moved the moment the emergency was raised: six left the
        // source's available bucket and landed in the destination's, valued at
        // the product master cost, with the co-signing manager on the ledger.
        List<MovementSnapshot> movements = await MovementsByReferenceAsync(transferId);
        movements.Should().HaveCount(2);
        movements.Sum(m => m.QuantityDelta).Should().Be(0m);
        movements.Should().OnlyContain(m =>
            m.MovementType == InventoryMovementType.TransferEmergency
            && m.ReferenceNumber == emergency.Number);
        movements.Single(m => m.LocationId == seed.StoreA.Value).QuantityDelta.Should().Be(-6m);
        movements.Single(m => m.LocationId == seed.StoreB.Value).QuantityDelta.Should().Be(6m);

        (await BalanceAsync(seed.StoreA, seed.Product, null, InventoryState.Available)).Should().Be(4m);
        (await BalanceSnapshotAsync(seed.StoreB, seed.Product, null, InventoryState.Available)).Quantity.Should().Be(6m);
        (await BalanceSnapshotAsync(seed.StoreB, seed.Product, null, InventoryState.Available)).AverageUnitCost.Should().Be(95m);

        // The queue offers the pending emergency to central review.
        using HttpResponseMessage pending = await GetAsync(
            client, "/api/v1/transfers/pending-central-review", coSigner);
        pending.StatusCode.Should().Be(HttpStatusCode.OK, await pending.Content.ReadAsStringAsync());
        using (JsonDocument document = JsonDocument.Parse(await pending.Content.ReadAsStringAsync()))
        {
            document.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("id").GetGuid())
                .Should().Contain(transferId);
        }

        // A store manager may not sit on the central review bench.
        using HttpResponseMessage denied = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/central-review"),
            new { approve = true },
            storeManager);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync());

        using HttpResponseMessage ratified = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/central-review"),
            new { approve = true, note = "Stock movement confirmed." },
            coSigner);
        ratified.StatusCode.Should().Be(HttpStatusCode.OK, await ratified.Content.ReadAsStringAsync());

        TransferDbo after = await TransferDboAsync(transferId);
        after.Status.Should().Be(TransferStatus.Received);
        after.CustodyKinds.Should().Equal("Created", "EmergencyCreated", "CentralReviewRatified");

        // Ratifying records the already-posted movement; it posts none itself.
        (await MovementsByReferenceAsync(transferId)).Should().HaveCount(2);
        (await BalanceAsync(seed.StoreA, seed.Product, null, InventoryState.Available)).Should().Be(4m);
        (await BalanceAsync(seed.StoreB, seed.Product, null, InventoryState.Available)).Should().Be(6m);

        // Reviewing again is a state error, not a double post.
        using HttpResponseMessage again = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/central-review"),
            new { approve = true },
            coSigner);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict, await again.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(again)).Should().Be("transfer.invalid_state");
    }

    [Fact]
    public async Task EmergencyTransfer_Rejected_ReversesEveryUnitBackToSource()
    {
        Seed seed = await SeedAsync("emg2");
        await factory.CreateUserAsync("emg2-a", Roles.MainInventoryManager, locations: [seed.StoreA], tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("emg2-b", Roles.MainInventoryManager, locations: [seed.StoreB], tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string bearer = await SignInAsync(client, "emg2-a");
        string reviewer = await SignInAsync(client, "emg2-b");

        await SeedAvailableAsync(seed.StoreA, seed.Product, batchId: null, quantity: 10m, unitCost: 95m);

        Guid transferId = await InitiateEmergencyAsync(
            client, bearer, seed, quantity: 6m, coSignerUserName: "emg2-b");
        Guid originalGroupId = (await MovementsByReferenceAsync(transferId))
            .Select(m => m.MovementGroupId).Distinct().Single();

        // Rejection demands a recorded reason.
        using HttpResponseMessage unreasoned = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/central-review"),
            new { approve = false },
            reviewer);
        unreasoned.StatusCode.Should().Be(HttpStatusCode.BadRequest, await unreasoned.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(unreasoned)).Should().Be("transfer.review_reject_reason_required");

        using HttpResponseMessage rejected = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/transfers/{transferId}/central-review"),
            new { approve = false, note = "Not approved: shelves are full." },
            reviewer);
        rejected.StatusCode.Should().Be(HttpStatusCode.OK, await rejected.Content.ReadAsStringAsync());

        TransferDbo after = await TransferDboAsync(transferId);
        after.Status.Should().Be(TransferStatus.Cancelled);
        after.CustodyKinds.Should().Equal("Created", "EmergencyCreated", "CentralReviewRejected");

        // The reversal group sits on top of the original movement and puts every
        // unit back: destination down six, source restored to ten.
        List<MovementSnapshot> reversal = await MovementsByReferenceAsync(transferId);
        reversal.Should().HaveCount(4);
        List<MovementSnapshot> reversalLegs = reversal
            .Where(m => m.MovementType == InventoryMovementType.TransferEmergencyReversal)
            .ToList();
        reversalLegs.Should().HaveCount(2);
        reversalLegs.Sum(m => m.QuantityDelta).Should().Be(0m);
        reversalLegs.Should().OnlyContain(m =>
            m.ReversesMovementGroupId == originalGroupId
            && m.ReferenceNumber == after.Number);
        reversalLegs.Single(m => m.LocationId == seed.StoreB.Value).QuantityDelta.Should().Be(-6m);
        reversalLegs.Single(m => m.LocationId == seed.StoreA.Value).QuantityDelta.Should().Be(6m);

        (await BalanceAsync(seed.StoreA, seed.Product, null, InventoryState.Available)).Should().Be(10m);
        (await BalanceAsync(seed.StoreB, seed.Product, null, InventoryState.Available)).Should().Be(0m);
    }

    [Fact]
    public async Task EmergencyTransfer_CoSignerRules_RefuseSamePersonAndOutOfScope()
    {
        Seed seed = await SeedAsync("emg3");
        await factory.CreateUserAsync("emg3-a", Roles.MainInventoryManager, locations: [seed.StoreA, seed.StoreB], tier: ApprovalTier.Unlimited);
        // Assigned to the source store only: a valid sign-in, but no authority
        // over the destination store.
        await factory.CreateUserAsync("emg3-out", Roles.MainInventoryManager, locations: [seed.StoreA], tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string bearer = await SignInAsync(client, "emg3-a");

        await SeedAvailableAsync(seed.StoreA, seed.Product, batchId: null, quantity: 10m, unitCost: 95m);

        // The same person cannot co-sign their own movement.
        using HttpResponseMessage same = await PostAsJsonAsync(
            client,
            "/api/v1/transfers/emergency",
            EmergencyBody(seed, quantity: 6m, coSignerUserName: "emg3-a"),
            bearer);
        same.StatusCode.Should().Be(HttpStatusCode.BadRequest, await same.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(same)).Should().Be("transfer.emergency_co_signer_same");

        // A manager without the destination store in scope is refused even
        // though their credentials are valid.
        using HttpResponseMessage outOfScope = await PostAsJsonAsync(
            client,
            "/api/v1/transfers/emergency",
            EmergencyBody(seed, quantity: 6m, coSignerUserName: "emg3-out"),
            bearer);
        outOfScope.StatusCode.Should().Be(HttpStatusCode.BadRequest, await outOfScope.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(outOfScope)).Should().Be("transfer.emergency_co_signer_unauthorized");

        // Bad credentials are refused the same way.
        using HttpResponseMessage bogus = await PostAsJsonAsync(
            client,
            "/api/v1/transfers/emergency",
            new
            {
                sourceLocationId = seed.StoreA.Value,
                destinationLocationId = seed.StoreB.Value,
                lines = new object[] { new { productId = seed.Product.Value, quantity = 6m } },
                coAuthorization = new { userName = "emg3-out", password = "wrong-password" },
                note = "Does not matter.",
            },
            bearer);
        bogus.StatusCode.Should().Be(HttpStatusCode.BadRequest, await bogus.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(bogus)).Should().Be("transfer.emergency_co_signer_unauthorized");

        // None of the refused attempts moved stock.
        (await BalanceAsync(seed.StoreA, seed.Product, null, InventoryState.Available)).Should().Be(10m);
        (await BalanceAsync(seed.StoreB, seed.Product, null, InventoryState.Available)).Should().Be(0m);
    }

    [Fact]
    public async Task EmergencyTransfer_MonthlyCap_RefusesBeyondAllowance()
    {
        Seed seed = await SeedAsync("emg4");
        await factory.CreateUserAsync("emg4-a", Roles.MainInventoryManager, locations: [seed.StoreA], tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("emg4-b", Roles.MainInventoryManager, locations: [seed.StoreB], tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string bearer = await SignInAsync(client, "emg4-a");

        await SeedAvailableAsync(seed.StoreA, seed.Product, batchId: null, quantity: 30m, unitCost: 95m);

        // The default allowance is three emergencies per store per month.
        for (int i = 1; i <= 3; i++)
        {
            Guid transferId = await InitiateEmergencyAsync(
                client, bearer, seed, quantity: 6m, coSignerUserName: "emg4-b");
            (await TransferDboAsync(transferId)).Status.Should().Be(TransferStatus.PendingCentralReview);
        }

        using HttpResponseMessage fourth = await PostAsJsonAsync(
            client,
            "/api/v1/transfers/emergency",
            EmergencyBody(seed, quantity: 6m, coSignerUserName: "emg4-b"),
            bearer);
        fourth.StatusCode.Should().Be(HttpStatusCode.Conflict, await fourth.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(fourth)).Should().Be("transfer.emergency_monthly_cap_exceeded");

        // The refused attempt left the third emergency's balances untouched.
        (await BalanceAsync(seed.StoreA, seed.Product, null, InventoryState.Available)).Should().Be(12m);
        (await BalanceAsync(seed.StoreB, seed.Product, null, InventoryState.Available)).Should().Be(18m);
    }

    [Fact]
    public async Task PreApprovalToken_EndToEnd_SubmitsStraightToApproved_AndIsSingleUse()
    {
        Seed seed = await SeedAsync("pat1");
        await factory.CreateUserAsync("pat1-issuer", Roles.MainInventoryManager, locations: [seed.StoreA, seed.StoreB], tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("pat1-requester", Roles.MainInventoryManager, locations: [seed.StoreA], tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string issuer = await SignInAsync(client, "pat1-issuer");
        string requester = await SignInAsync(client, "pat1-requester");

        await SeedAvailableAsync(seed.StoreA, seed.Product, batchId: null, quantity: 10m, unitCost: 95m);

        Guid tokenId = await IssueTokenAsync(
            client, issuer, seed, products: [seed.Product], maxValue: 600m);

        PreApprovalTokenDbo issued = await PreApprovalTokenDboAsync(tokenId);
        issued.Status.Should().Be(PreApprovalTokenStatus.Draft);
        issued.Number.Should().MatchRegex(@"^PAT-\d{4}-\d{6}$");

        // The token stands in for the review queue: submission under the
        // issuer's authority lands the transfer directly in Approved.
        using HttpResponseMessage createdResponse = await PostAsJsonAsync(
            client,
            "/api/v1/transfers",
            TransferBody(seed, quantity: 6m, tokenId),
            requester);
        createdResponse.StatusCode.Should().Be(HttpStatusCode.OK, await createdResponse.Content.ReadAsStringAsync());
        using (JsonDocument document = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync()))
        {
            Guid transferId = document.RootElement.GetProperty("id").GetGuid();

            TransferDbo draft = await TransferDboAsync(transferId);
            draft.Status.Should().Be(TransferStatus.Draft);
            draft.Mode.Should().Be(TransferMode.PreApproved);
            draft.PreApprovalTokenId.Should().Be(tokenId);
            draft.Number.Should().BeEmpty();

            using HttpResponseMessage submitted = await PostAsync(
                client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/submit"), requester);
            submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());

            TransferDbo approved = await TransferDboAsync(transferId);
            approved.Status.Should().Be(TransferStatus.Approved);
            approved.CustodyKinds.Should().Equal("Created", "PreApprovedApproval");

            PreApprovalTokenDbo consumed = await PreApprovalTokenDboAsync(tokenId);
            consumed.Status.Should().Be(PreApprovalTokenStatus.Consumed);
            consumed.ConsumedByTransferId.Should().Be(transferId);

            // The consumed token cannot back a second transfer, even at draft.
            using HttpResponseMessage replay = await PostAsJsonAsync(
                client,
                "/api/v1/transfers",
                TransferBody(seed, quantity: 6m, tokenId),
                requester);
            replay.StatusCode.Should().Be(HttpStatusCode.Conflict, await replay.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(replay)).Should().Be("preapproval.already_consumed");

            // The remaining lifecycle is the ordinary picking pipeline.
            await PickReadyDispatchAsync(client, requester, transferId, [(null, 6m)]);
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
                requester);
            received.StatusCode.Should().Be(HttpStatusCode.OK, await received.Content.ReadAsStringAsync());

            using HttpResponseMessage verified = await PostAsync(
                client, FormattableString.Invariant($"/api/v1/transfers/{transferId}/verify"), requester);
            verified.StatusCode.Should().Be(HttpStatusCode.OK, await verified.Content.ReadAsStringAsync());

            (await TransferDboAsync(transferId)).Status.Should().Be(TransferStatus.Closed);
        }
    }

    [Fact]
    public async Task PreApprovalToken_RouteProductValueAndWindow_AreGuardRailed()
    {
        Seed seed = await SeedAsync("pat2");
        await factory.CreateUserAsync("pat2-issuer", Roles.MainInventoryManager, locations: [seed.StoreA, seed.StoreB], tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("pat2-requester", Roles.MainInventoryManager, locations: [seed.StoreA], tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string issuer = await SignInAsync(client, "pat2-issuer");
        string requester = await SignInAsync(client, "pat2-requester");

        string otherBarcode = Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture);
        ProductId otherProduct = await factory.CreateProductAsync(
            $"PA-{seed.Suffix}", $"Other {seed.Suffix}", otherBarcode, defaultPurchaseCost: 95m);

        // Value ceiling: six units at 95 each exceed a 500 ceiling.
        Guid capped = await IssueTokenAsync(client, issuer, seed, products: [seed.Product], maxValue: 500m);
        using HttpResponseMessage tooRich = await PostAsJsonAsync(
            client,
            "/api/v1/transfers",
            TransferBody(seed, quantity: 6m, capped),
            requester);
        tooRich.StatusCode.Should().Be(HttpStatusCode.Conflict, await tooRich.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(tooRich)).Should().Be("preapproval.value_exceeded");

        // Product scope: the token covers only the seeded product.
        Guid scoped = await IssueTokenAsync(
            client, issuer, seed, products: [seed.Product], maxValue: null);
        using HttpResponseMessage foreignProduct = await PostAsJsonAsync(
            client,
            "/api/v1/transfers",
            new
            {
                sourceLocationId = seed.StoreA.Value,
                destinationLocationId = seed.StoreB.Value,
                lines = new object[] { new { productId = otherProduct.Value, quantity = 1m } },
                preApprovalTokenId = scoped,
            },
            requester);
        foreignProduct.StatusCode.Should().Be(HttpStatusCode.BadRequest, await foreignProduct.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(foreignProduct)).Should().Be("preapproval.scope_mismatch");

        // Route scope: a token for StoreA -> StoreB cannot leave the warehouse.
        using HttpResponseMessage wrongRoute = await PostAsJsonAsync(
            client,
            "/api/v1/transfers",
            new
            {
                sourceLocationId = seed.Warehouse.Value,
                destinationLocationId = seed.StoreB.Value,
                lines = new object[] { new { productId = seed.Product.Value, quantity = 1m } },
                preApprovalTokenId = scoped,
            },
            requester);
        wrongRoute.StatusCode.Should().Be(HttpStatusCode.BadRequest, await wrongRoute.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(wrongRoute)).Should().Be("preapproval.scope_mismatch");

        // A token whose window has not opened yet cannot back a transfer.
        DateTimeOffset laterNow = DateTimeOffset.UtcNow;
        Guid future = await IssueTokenAsync(
            client, issuer, seed, products: [], maxValue: null,
            validFromUtc: laterNow.AddHours(1), validUntilUtc: laterNow.AddDays(7));
        using HttpResponseMessage early = await PostAsJsonAsync(
            client,
            "/api/v1/transfers",
            TransferBody(seed, quantity: 1m, future),
            requester);
        early.StatusCode.Should().Be(HttpStatusCode.Conflict, await early.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(early)).Should().Be("preapproval.not_yet_valid");
    }

    [Fact]
    public async Task PreApprovalToken_Revocation_PreventsUse()
    {
        Seed seed = await SeedAsync("pat3");
        await factory.CreateUserAsync("pat3-issuer", Roles.MainInventoryManager, locations: [seed.StoreA, seed.StoreB], tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("pat3-requester", Roles.MainInventoryManager, locations: [seed.StoreA], tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string issuer = await SignInAsync(client, "pat3-issuer");
        string requester = await SignInAsync(client, "pat3-requester");

        Guid tokenId = await IssueTokenAsync(client, issuer, seed, products: [], maxValue: null);

        // Standing down demands a recorded reason.
        using HttpResponseMessage unreasoned = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/pre-approvals/{tokenId}/revoke"),
            new { reason = (string?)null },
            issuer);
        unreasoned.StatusCode.Should().Be(HttpStatusCode.BadRequest, await unreasoned.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(unreasoned)).Should().Be("preapproval.revoke_reason_required");

        using HttpResponseMessage revoked = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/pre-approvals/{tokenId}/revoke"),
            new { reason = "Budget frozen for the month." },
            issuer);
        revoked.StatusCode.Should().Be(HttpStatusCode.OK, await revoked.Content.ReadAsStringAsync());

        (await PreApprovalTokenDboAsync(tokenId)).Status.Should().Be(PreApprovalTokenStatus.Revoked);

        using HttpResponseMessage create = await PostAsJsonAsync(
            client,
            "/api/v1/transfers",
            TransferBody(seed, quantity: 1m, tokenId),
            requester);
        create.StatusCode.Should().Be(HttpStatusCode.Conflict, await create.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(create)).Should().Be("preapproval.revoked");

        // The list endpoint reflects the revocation.
        using HttpResponseMessage list = await GetAsync(client, "/api/v1/pre-approvals", issuer);
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        using (JsonDocument document = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            document.RootElement.EnumerateArray()
                .Single(t => t.GetProperty("id").GetGuid() == tokenId)
                .GetProperty("status").GetString()
                .Should().Be("Revoked");
        }
    }

    [Fact]
    public async Task PreApprovalToken_Issue_ValidatesRouteAndWindowOnCreation()
    {
        Seed seed = await SeedAsync("pat4");
        await factory.CreateUserAsync("pat4-issuer", Roles.MainInventoryManager, locations: [seed.StoreA, seed.StoreB], tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("pat4-store", Roles.StoreManager, locations: [seed.StoreA]);
        using HttpClient client = factory.CreateClient();
        string issuer = await SignInAsync(client, "pat4-issuer");
        string storeManager = await SignInAsync(client, "pat4-store");

        DateTimeOffset now = DateTimeOffset.UtcNow;

        using HttpResponseMessage sameLocation = await PostAsJsonAsync(
            client,
            "/api/v1/pre-approvals",
            TokenBody(
                seed,
                products: [],
                maxValue: null,
                validFromUtc: now.AddMinutes(-1),
                validUntilUtc: now.AddDays(7),
                destinationLocationId: seed.StoreA.Value),
            issuer);
        sameLocation.StatusCode.Should().Be(HttpStatusCode.BadRequest, await sameLocation.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(sameLocation)).Should().Be("preapproval.same_location");

        using HttpResponseMessage negativeCeiling = await PostAsJsonAsync(
            client,
            "/api/v1/pre-approvals",
            TokenBody(seed, products: [], maxValue: -1m, validFromUtc: now.AddMinutes(-1), validUntilUtc: now.AddDays(7)),
            issuer);
        negativeCeiling.StatusCode.Should().Be(HttpStatusCode.BadRequest, await negativeCeiling.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(negativeCeiling)).Should().Be("preapproval.max_value_invalid");

        using HttpResponseMessage backwardsWindow = await PostAsJsonAsync(
            client,
            "/api/v1/pre-approvals",
            TokenBody(seed, products: [], maxValue: null, validFromUtc: now.AddDays(7), validUntilUtc: now.AddMinutes(-1)),
            issuer);
        backwardsWindow.StatusCode.Should().Be(HttpStatusCode.BadRequest, await backwardsWindow.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(backwardsWindow)).Should().Be("preapproval.invalid_window");

        using HttpResponseMessage expired = await PostAsJsonAsync(
            client,
            "/api/v1/pre-approvals",
            TokenBody(seed, products: [], maxValue: null, validFromUtc: now.AddDays(-1), validUntilUtc: now.AddMinutes(-1)),
            issuer);
        expired.StatusCode.Should().Be(HttpStatusCode.Conflict, await expired.Content.ReadAsStringAsync());
        (await ReadErrorCodeAsync(expired)).Should().Be("preapproval.expired_at_issue");

        // Issuing is a head office privilege; a store manager is stopped at the
        // door regardless of the body.
        using HttpResponseMessage denied = await PostAsJsonAsync(
            client,
            "/api/v1/pre-approvals",
            TokenBody(seed, products: [], maxValue: null, validFromUtc: now.AddMinutes(-1), validUntilUtc: now.AddDays(7)),
            storeManager);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReplenishmentRecommendations_PrefersWarehouseSurplus_AndScopesToCaller()
    {
        Seed seed = await SeedAsync("rpl1");
        await factory.CreateUserAsync("rpl1-b", Roles.MainInventoryManager, locations: [seed.StoreA, seed.StoreB], tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("rpl1-store", Roles.MainInventoryManager, locations: [seed.StoreA], tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string storeOnly = await SignInAsync(client, "rpl1-store");
        string both = await SignInAsync(client, "rpl1-b");

        // Store A runs critical: six on hand against a minimum of ten and a
        // reorder point of fifteen. The warehouse holds fifty.
        await SeedSettingAsync(seed.StoreA, seed.Product, minimum: 10m, reorder: 15m, target: 30m, maximum: 40m, preferred: 12m);
        await SeedSettingAsync(seed.StoreB, seed.Product, minimum: 5m, reorder: 10m, target: 20m, maximum: 30m, preferred: 10m);
        await SeedAvailableAsync(seed.StoreA, seed.Product, batchId: null, quantity: 6m, unitCost: 95m);
        await SeedAvailableAsync(seed.StoreB, seed.Product, batchId: null, quantity: 20m, unitCost: 95m);
        await SeedAvailableAsync(seed.Warehouse, seed.Product, batchId: null, quantity: 50m, unitCost: 95m);

        // The caller assigned only to Store A sees exactly its recommendation:
        // warehouse first, quantity capped by the preferred batch.
        using HttpResponseMessage scoped = await GetAsync(client, "/api/v1/replenishment/recommendations", storeOnly);
        scoped.StatusCode.Should().Be(HttpStatusCode.OK, await scoped.Content.ReadAsStringAsync());
        List<RecommendationDbo> seen = JsonSerializer.Deserialize<List<RecommendationDbo>>(
            await scoped.Content.ReadAsStringAsync(),
            WebJsonOptions)!;
        seen.Should().ContainSingle();
        seen[0].LocationId.Should().Be(seed.StoreA.Value);
        seen[0].ProductId.Should().Be(seed.Product.Value);
        seen[0].Available.Should().Be(6m);
        seen[0].Deficit.Should().Be(24m);
        seen[0].Urgency.Should().Be("Critical");
        seen[0].SuggestedSourceLocationId.Should().Be(seed.Warehouse.Value);
        seen[0].SuggestedQuantity.Should().Be(12m);
    }

    [Fact]
    public async Task ReplenishmentRecommendations_BusinessWideOwnerWithoutAssignments_SeesEveryStore()
    {
        // An owner covers the whole business and has no store assignments; the
        // recommendations once scoped to assignments only and came back empty.
        Seed seed = await SeedAsync("rpl3");
        await factory.CreateUserAsync("rpl3-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "rpl3-owner");

        await SeedSettingAsync(seed.StoreA, seed.Product, minimum: 10m, reorder: 15m, target: 30m, maximum: 40m, preferred: 12m);
        await SeedAvailableAsync(seed.StoreA, seed.Product, batchId: null, quantity: 4m, unitCost: 95m);
        await SeedAvailableAsync(seed.Warehouse, seed.Product, batchId: null, quantity: 50m, unitCost: 95m);

        using HttpResponseMessage response = await GetAsync(client, "/api/v1/replenishment/recommendations", owner);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        List<RecommendationDbo> recommendations = JsonSerializer.Deserialize<List<RecommendationDbo>>(
            await response.Content.ReadAsStringAsync(),
            WebJsonOptions)!;

        recommendations.Should().Contain(r => r.LocationId == seed.StoreA.Value && r.ProductId == seed.Product.Value);
    }

    [Fact]
    public async Task ReplenishmentRecommendations_FallsBackToSurplusStore()
    {
        Seed seed = await SeedAsync("rpl2");
        LocationId storeB2 = await factory.CreateLocationAsync($"ST2-{seed.Suffix}", $"Store 2 {seed.Suffix}", LocationKind.Store);
        await factory.CreateUserAsync("rpl2-both", Roles.MainInventoryManager, locations: [seed.StoreA, storeB2], tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string both = await SignInAsync(client, "rpl2-both");

        // Store A is High (twelve against a minimum of ten) and the warehouse is
        // dry; stock must come from Store B 2, which holds twenty above its five.
        await SeedSettingAsync(seed.StoreA, seed.Product, minimum: 10m, reorder: 15m, target: 30m, maximum: 40m, preferred: 12m);
        await SeedSettingAsync(storeB2, seed.Product, minimum: 5m, reorder: 10m, target: 20m, maximum: 30m, preferred: 10m);
        await SeedAvailableAsync(seed.StoreA, seed.Product, batchId: null, quantity: 12m, unitCost: 95m);
        await SeedAvailableAsync(storeB2, seed.Product, batchId: null, quantity: 20m, unitCost: 95m);
        // The warehouse deliberately holds nothing.

        using HttpResponseMessage response = await GetAsync(client, "/api/v1/replenishment/recommendations", both);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        List<RecommendationDbo> recommendations = JsonSerializer.Deserialize<List<RecommendationDbo>>(
            await response.Content.ReadAsStringAsync(),
            WebJsonOptions)!;

        // Store B 2 is above its reorder point, so the only recommendation is
        // Store A, sourcing from Store B 2 and capped by the preferred batch.
        recommendations.Should().ContainSingle();
        string raw2 = await response.Content.ReadAsStringAsync();
        recommendations[0].LocationId.Should().Be(seed.StoreA.Value);
        recommendations[0].Available.Should().Be(12m);
        recommendations[0].Deficit.Should().Be(18m);
        recommendations[0].Urgency.Should().Be("High");
        recommendations[0].SuggestedSourceLocationId.Should().Be(storeB2.Value);
        recommendations[0].SuggestedQuantity.Should().Be(12m);
    }

    // ---- Setup helpers -------------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix)
    {
        SupplierId supplier = await factory.CreateSupplierAsync($"SUP-{suffix}", $"Supplier {suffix}");
        LocationId warehouse = await factory.CreateLocationAsync($"WH-{suffix}", $"Warehouse {suffix}", LocationKind.MainWarehouse);
        LocationId storeA = await factory.CreateLocationAsync($"ST-{suffix}", $"Store {suffix}", LocationKind.Store);
        LocationId storeB = await factory.CreateLocationAsync($"ST-{suffix}b", $"Store B {suffix}", LocationKind.Store);
        string barcode = Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture);
        ProductId product = await factory.CreateProductAsync($"TR-{suffix}", $"Product {suffix}", barcode, defaultPurchaseCost: 95m);

        return new Seed(supplier, warehouse, storeA, storeB, product, suffix);
    }

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

    private Task<ProductLocationSettingId> SeedSettingAsync(
        LocationId locationId, ProductId productId,
        decimal minimum, decimal reorder, decimal target, decimal maximum, decimal preferred)
        => factory.WithServiceAsync(async context =>
        {
            Result<ProductLocationSetting> created = ProductLocationSetting.Create(
                productId, locationId, isStocked: true, minimum, reorder, target, maximum, preferred);

            created.IsSuccess.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));

            context.ProductLocationSettings.Add(created.Value);
            await context.SaveChangesAsync();
            return created.Value.Id;
        });

    // ---- Request helpers -----------------------------------------------------

    private static object EmergencyBody(Seed seed, decimal quantity, string coSignerUserName)
        => new
        {
            sourceLocationId = seed.StoreA.Value,
            destinationLocationId = seed.StoreB.Value,
            lines = new object[] { new { productId = seed.Product.Value, quantity } },
            coAuthorization = new { userName = coSignerUserName, password = PosApiFactory.TestPassword },
            note = "Emergency: shelves are empty.",
        };

    private static object TransferBody(Seed seed, decimal quantity, Guid? tokenId = null)
        => new
        {
            sourceLocationId = seed.StoreA.Value,
            destinationLocationId = seed.StoreB.Value,
            lines = new object[] { new { productId = seed.Product.Value, quantity } },
            preApprovalTokenId = tokenId,
        };

    private static object TokenBody(
        Seed seed, IReadOnlyList<Guid> products, decimal? maxValue,
        DateTimeOffset validFromUtc, DateTimeOffset validUntilUtc,
        Guid? destinationLocationId = null)
        => new
        {
            sourceLocationId = seed.StoreA.Value,
            destinationLocationId = destinationLocationId ?? seed.StoreB.Value,
            products,
            maxValue,
            validFromUtc,
            validUntilUtc,
            note = (string?)null,
        };

    private static async Task<Guid> InitiateEmergencyAsync(
        HttpClient client, string accessToken, Seed seed, decimal quantity, string coSignerUserName)
    {
        using HttpResponseMessage response = await PostAsJsonAsync(
            client, "/api/v1/transfers/emergency", EmergencyBody(seed, quantity, coSignerUserName), accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> IssueTokenAsync(
        HttpClient client, string accessToken, Seed seed,
        IReadOnlyList<ProductId> products, decimal? maxValue,
        DateTimeOffset? validFromUtc = null, DateTimeOffset? validUntilUtc = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using HttpResponseMessage response = await PostAsJsonAsync(
            client,
            "/api/v1/pre-approvals",
            TokenBody(
                seed,
                [.. products.Select(p => p.Value)],
                maxValue,
                validFromUtc ?? now.AddMinutes(-1),
                validUntilUtc ?? now.AddDays(7)),
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
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

    // ---- Read helpers --------------------------------------------------------

    private async Task<TransferDbo> TransferDboAsync(Guid transferId)
        => await factory.WithServiceAsync(async context =>
        {
            Transfer? transfer = await context.Transfers
                .AsNoTracking()
                .Include(t => t.CustodyEvents)
                .FirstOrDefaultAsync(t => t.Id == new TransferOrderId(transferId));

            transfer.Should().NotBeNull();

            return new TransferDbo(
                transfer!.Status,
                transfer.Mode,
                transfer.Number,
                transfer.PreApprovalTokenId?.Value,
                transfer.EmergencyLedgerGroupId?.Value,
                [.. transfer.CustodyEvents.OrderBy(e => e.Sequence).Select(e => e.Kind.ToString())]);
        });

    private async Task<PreApprovalTokenDbo> PreApprovalTokenDboAsync(Guid tokenId)
        => await factory.WithServiceAsync(async context =>
        {
            PreApprovalToken? token = await context.PreApprovalTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == new PreApprovalTokenId(tokenId));

            token.Should().NotBeNull();

            return new PreApprovalTokenDbo(
                token!.Status,
                token.Number,
                token.ConsumedByTransferId?.Value);
        });

    private async Task<List<MovementSnapshot>> MovementsByReferenceAsync(Guid referenceDocumentId)
        => await factory.WithServiceAsync(context => context.InventoryMovements
            .Where(m => m.ReferenceDocumentId == referenceDocumentId)
            .OrderBy(m => m.QuantityDelta)
            .Select(m => new MovementSnapshot(
                m.QuantityDelta,
                m.State,
                m.MovementType,
                m.MovementGroupId.Value,
                m.ReversesMovementGroupId != null ? m.ReversesMovementGroupId.Value.Value : null,
                m.ReferenceNumber,
                m.LocationId.Value))
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

    // ---- DTOs -----------------------------------------------------------------

    private sealed record MovementSnapshot(
        decimal QuantityDelta,
        InventoryState State,
        InventoryMovementType MovementType,
        Guid MovementGroupId,
        Guid? ReversesMovementGroupId,
        string ReferenceNumber,
        Guid LocationId);

    private sealed record TransferDbo(
        TransferStatus Status,
        TransferMode Mode,
        string Number,
        Guid? PreApprovalTokenId,
        Guid? EmergencyLedgerGroupId,
        IReadOnlyList<string> CustodyKinds);

    private sealed record PreApprovalTokenDbo(
        PreApprovalTokenStatus Status,
        string Number,
        Guid? ConsumedByTransferId);

    private sealed record BalanceSnapshot(decimal Quantity, decimal AverageUnitCost);

    private sealed record RecommendationDbo(
        Guid LocationId,
        Guid ProductId,
        decimal Available,
        decimal Deficit,
        string Urgency,
        Guid? SuggestedSourceLocationId,
        decimal SuggestedQuantity);

    private sealed record Seed(
        SupplierId Supplier,
        LocationId Warehouse,
        LocationId StoreA,
        LocationId StoreB,
        ProductId Product,
        string Suffix);
}
