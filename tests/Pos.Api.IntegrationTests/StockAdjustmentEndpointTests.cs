using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using static Pos.Api.IntegrationTests.InventoryControlTestSupport;
using static Pos.Api.IntegrationTests.UserAdministrationEndpointTests;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Stock adjustments through the real pipeline: nothing reaches the ledger until
/// someone other than the author approves it within their value tier and their
/// locations; posting balances against EXT-WRITEOFF; a reversal restores the stock.
/// </summary>
[Collection("api")]
public sealed class StockAdjustmentEndpointTests(PosApiFactory factory)
{
    private const string Adjustments = "/api/v1/inventory/adjustments";

    [Fact]
    public async Task DamageWriteOff_IsApprovedBySomeoneElse_PostsToWriteOff_AndCanBeReversed()
    {
        LocationId store = await factory.CreateLocationAsync("ADJ-S1", "Adjustment Store 1");
        LocationId writeOff = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);
        ProductId product = await factory.CreateProductAsync("ADJ-01", "Glass Jar Pickles", "4800000500018", defaultPurchaseCost: 50m);
        await factory.CreateUserAsync("adj1-staff", Roles.InventoryStaff, locations: [store]);
        await factory.CreateUserAsync("adj1-manager", Roles.StoreManager, locations: [store], tier: ApprovalTier.Tier1);
        using HttpClient client = factory.CreateClient();

        string staff = await SignInAsync(client, "adj1-staff");
        string manager = await SignInAsync(client, "adj1-manager");
        await SetBucketAsync(factory, store, product, 20m, 50m);

        Guid id = await CreateAsync(client, staff, new
        {
            locationId = store.Value,
            reason = (int)AdjustmentReasonCode.Damaged,
            lines = new[] { new { productId = product.Value, state = (int)InventoryState.Available, quantityDelta = -3m } },
        });

        using (JsonDocument draft = await GetJsonAsync(client, $"{Adjustments}/{id}", staff))
        {
            JsonElement root = draft.RootElement;
            root.GetProperty("adjustment").GetProperty("status").GetString().Should().Be("Draft");
            root.GetProperty("adjustment").GetProperty("totalAbsoluteValue").GetDecimal().Should().Be(150m);
            root.GetProperty("lines")[0].GetProperty("movementType").GetString().Should().Be("Damage");
        }

        await ExpectAsync(client, $"{Adjustments}/{id}/submit", staff, null, HttpStatusCode.OK);
        await ExpectAsync(client, $"{Adjustments}/{id}/approve", staff, null, HttpStatusCode.Forbidden);
        await ExpectAsync(client, $"{Adjustments}/{id}/approve", manager, null, HttpStatusCode.OK);

        (await QuantityAsync(factory, store, product)).Should().Be(17m);

        List<InventoryMovement> posted = await MovementsAsync(factory, id);
        posted.Should().HaveCount(2).And.OnlyContain(m => m.MovementType == InventoryMovementType.Damage);
        posted.Single(m => m.LocationId == writeOff).QuantityDelta.Should().Be(3m);
        posted.Should().OnlyContain(m => m.ReferenceNumber.StartsWith("ADJ-", StringComparison.Ordinal)
                                         && m.ReasonCode == AdjustmentReasonCode.Damaged);

        await ExpectAsync(client, $"{Adjustments}/{id}/reverse", manager, new { reason = "x" }, HttpStatusCode.BadRequest, "inventory_control.reason_required");
        await ExpectAsync(client, $"{Adjustments}/{id}/reverse", manager, new { reason = "Jars were only dusty, not broken" }, HttpStatusCode.OK);

        (await QuantityAsync(factory, store, product)).Should().Be(20m);
        (await MovementsAsync(factory, id)).Count(m => m.MovementType == InventoryMovementType.Reversal).Should().Be(2);

        List<string> actions = await factory.WithServiceAsync(context => context.AuditLog
            .Where(a => a.EntityId == id).Select(a => a.Action).ToListAsync());
        actions.Should().Contain([
            AuditActions.Inventory.AdjustmentCreated,
            AuditActions.Inventory.AdjustmentApproved,
            AuditActions.Inventory.AdjustmentReversed]);
    }

    [Fact]
    public async Task Approval_IsBoundedByTier_Location_AndSegregationOfDuties()
    {
        LocationId store = await factory.CreateLocationAsync("ADJ-S2", "Adjustment Store 2");
        LocationId otherStore = await factory.CreateLocationAsync("ADJ-S3", "Adjustment Store 3");
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);
        ProductId product = await factory.CreateProductAsync("ADJ-02", "Bottled Cooking Oil", "4800000500025", defaultPurchaseCost: 50m);
        await factory.CreateUserAsync("adj2-staff", Roles.InventoryStaff, locations: [store]);
        await factory.CreateUserAsync("adj2-manager", Roles.StoreManager, locations: [store], tier: ApprovalTier.Tier1);
        await factory.CreateUserAsync("adj2-other", Roles.StoreManager, locations: [otherStore], tier: ApprovalTier.Tier1);
        await factory.CreateUserAsync("adj2-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();

        string staff = await SignInAsync(client, "adj2-staff");
        string manager = await SignInAsync(client, "adj2-manager");
        string other = await SignInAsync(client, "adj2-other");
        string owner = await SignInAsync(client, "adj2-owner");
        await SetBucketAsync(factory, store, product, 500m, 50m);

        // 200 units at 50 is 10,000 — above a Tier 1 store manager's 5,000 ceiling.
        Guid large = await CreateSubmittedAsync(client, staff, store, product, AdjustmentReasonCode.Loss, -200m);
        await ExpectAsync(client, $"{Adjustments}/{large}/approve", manager, null, HttpStatusCode.Forbidden, "approval.tier_exceeded");
        await ExpectAsync(client, $"{Adjustments}/{large}/approve", other, null, HttpStatusCode.Forbidden, "approval.approver_out_of_scope");
        await ExpectAsync(client, $"{Adjustments}/{large}/approve", owner, null, HttpStatusCode.OK);
        (await QuantityAsync(factory, store, product)).Should().Be(300m);

        Guid own = await CreateSubmittedAsync(client, manager, store, product, AdjustmentReasonCode.Spoilage, -1m);
        await ExpectAsync(client, $"{Adjustments}/{own}/approve", manager, null, HttpStatusCode.Forbidden, "approval.self_approval_refused");
        await ExpectAsync(client, $"{Adjustments}/{own}/reject", other, new { reason = "Not my store" }, HttpStatusCode.Forbidden, "inventory_control.outside_scope");
        await ExpectAsync(client, $"{Adjustments}/{own}/reject", owner, new { reason = "Spoilage not photographed" }, HttpStatusCode.OK);

        using (JsonDocument rejected = await GetJsonAsync(client, $"{Adjustments}?locationId={store.Value}&status=3", staff))
        {
            rejected.RootElement.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).Should().Contain(own);
        }

        using (JsonDocument hidden = await GetJsonAsync(client, $"{Adjustments}?locationId={store.Value}", other))
        {
            hidden.RootElement.GetArrayLength().Should().Be(0, "another store's adjustments are not visible");
        }

        (await QuantityAsync(factory, store, product)).Should().Be(300m, "a rejected adjustment posts nothing");
    }

    [Fact]
    public async Task InvalidRequests_AreRefused_AndAnApprovalThatWouldGoNegativeRollsBack()
    {
        LocationId store = await factory.CreateLocationAsync("ADJ-S4", "Adjustment Store 4");
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);
        ProductId product = await factory.CreateProductAsync("ADJ-03", "Canned Corned Beef", "4800000500032", defaultPurchaseCost: 30m);
        await factory.CreateUserAsync("adj3-staff", Roles.InventoryStaff, locations: [store]);
        await factory.CreateUserAsync("adj3-manager", Roles.StoreManager, locations: [store], tier: ApprovalTier.Tier1);
        using HttpClient client = factory.CreateClient();

        string staff = await SignInAsync(client, "adj3-staff");
        string manager = await SignInAsync(client, "adj3-manager");
        await SetBucketAsync(factory, store, product, 2m, 30m);

        await ExpectAsync(client, Adjustments, staff, new
        {
            locationId = store.Value,
            reason = (int)AdjustmentReasonCode.Theft,
            lines = new[] { new { productId = product.Value, state = (int)InventoryState.Available, quantityDelta = 5m } },
        }, HttpStatusCode.BadRequest, "adjustment.must_remove_stock");

        Guid tooMuch = await CreateSubmittedAsync(client, staff, store, product, AdjustmentReasonCode.Theft, -5m);
        await ExpectAsync(client, $"{Adjustments}/{tooMuch}/approve", manager, null, HttpStatusCode.Conflict, "inventory.insufficient_stock");

        using JsonDocument detail = await GetJsonAsync(client, $"{Adjustments}/{tooMuch}", manager);
        detail.RootElement.GetProperty("adjustment").GetProperty("status").GetString().Should().Be("PendingApproval");
        detail.RootElement.GetProperty("adjustment").GetProperty("number").GetString().Should().BeEmpty();
        (await QuantityAsync(factory, store, product)).Should().Be(2m);
    }

    private static async Task<Guid> CreateSubmittedAsync(
        HttpClient client, string token, LocationId store, ProductId product, AdjustmentReasonCode reason, decimal delta)
    {
        Guid id = await CreateAsync(client, token, new
        {
            locationId = store.Value,
            reason = (int)reason,
            lines = new[] { new { productId = product.Value, state = (int)InventoryState.Available, quantityDelta = delta } },
        });

        await ExpectAsync(client, $"{Adjustments}/{id}/submit", token, null, HttpStatusCode.OK);
        return id;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string token, object body)
    {
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, Adjustments, token, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).RootElement.GetProperty("id").GetGuid();
    }

    internal static async Task ExpectAsync(
        HttpClient client, string path, string token, object? body, HttpStatusCode status, string? errorCode = null)
    {
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, path, token, body);
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());

        if (errorCode is not null)
        {
            (await ReadErrorCodeAsync(response)).Should().Be(errorCode);
        }
    }
}
