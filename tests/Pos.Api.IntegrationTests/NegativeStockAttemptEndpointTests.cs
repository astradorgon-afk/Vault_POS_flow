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
using static Pos.Api.IntegrationTests.UserAdministrationEndpointTests;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Refused stock draws recorded and reported through the real pipeline.
/// </summary>
/// <remarks>
/// The refusal rolls the whole command back, so the record must be written after
/// the transaction ends or it vanishes with it. The scenario is the one the
/// report exists for: stock that was there when the transfer was picked is gone
/// by dispatch, and someone tries to ship it anyway — twice.
/// </remarks>
[Collection("api")]
public sealed class NegativeStockAttemptEndpointTests(PosApiFactory factory)
{
    private const string Report = "/api/v1/inventory/exceptions/negative-attempts";

    [Fact]
    public async Task RefusedDispatch_IsRecordedDespiteTheRollback_AndRepeatedAttemptsAreRanked()
    {
        LocationId warehouse = await factory.CreateLocationAsync("NSA-WH", "Shrinkage Warehouse", LocationKind.MainWarehouse);
        LocationId store = await factory.CreateLocationAsync("NSA-ST", "Shrinkage Store");
        ProductId product = await factory.CreateProductAsync("NSA-01", "Premium Coffee Beans 1kg", "4800000400011", defaultPurchaseCost: 95m);
        await factory.CreateUserAsync("nsa-requester", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("nsa-approver", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("nsa-owner", Roles.Owner);
        await factory.CreateUserAsync("nsa-store", Roles.StoreManager, locations: [store]);
        using HttpClient client = factory.CreateClient();

        string requester = await SignInAsync(client, "nsa-requester");
        string approver = await SignInAsync(client, "nsa-approver");
        string owner = await SignInAsync(client, "nsa-owner");
        string storeManager = await SignInAsync(client, "nsa-store");

        await SetAvailableAsync(warehouse, product, 10m, insert: true);
        Guid transferId = await PrepareTransferForDispatchAsync(client, requester, approver, warehouse, store, product);

        // Six were picked; only one is left when the truck is loaded.
        await SetAvailableAsync(warehouse, product, 1m, insert: false);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpResponseMessage refused = await SendAsync(
                client, HttpMethod.Post, $"/api/v1/transfers/{transferId}/dispatch", approver);
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(refused)).Should().Be("inventory.insufficient_stock");
        }

        int movements = await factory.WithServiceAsync(context => context.InventoryMovements
            .CountAsync(m => m.ProductId == product));
        movements.Should().Be(0, "the refused dispatch rolled back and posted nothing");

        using (JsonDocument listed = await GetJsonAsync(client, $"{Report}?productId={product.Value}", owner))
        {
            JsonElement[] rows = [.. listed.RootElement.EnumerateArray()];
            rows.Should().HaveCount(2);

            JsonElement row = rows[0];
            row.GetProperty("locationCode").GetString().Should().Be("NSA-WH");
            row.GetProperty("sku").GetString().Should().Be("NSA-01");
            row.GetProperty("state").GetString().Should().Be(nameof(InventoryState.Available));
            row.GetProperty("movementType").GetString().Should().Be(nameof(InventoryMovementType.TransferDispatch));
            row.GetProperty("requestedQuantity").GetDecimal().Should().Be(6m);
            row.GetProperty("availableQuantity").GetDecimal().Should().Be(1m);
            row.GetProperty("shortfall").GetDecimal().Should().Be(5m);
            row.GetProperty("policy").GetString().Should().Be(nameof(NegativeStockPolicy.Prohibit));
        }

        using (JsonDocument summary = await GetJsonAsync(client, $"{Report}/summary?locationId={warehouse.Value}", owner))
        {
            JsonElement row = summary.RootElement.EnumerateArray().Should().ContainSingle().Subject;
            row.GetProperty("productId").GetGuid().Should().Be(product.Value);
            row.GetProperty("attempts").GetInt32().Should().Be(2);
            row.GetProperty("totalShortfall").GetDecimal().Should().Be(10m);
        }

        int audited = await factory.WithServiceAsync(context => context.AuditLog
            .CountAsync(a => a.Action == AuditActions.Inventory.NegativeStockAttempted && a.LocationId == warehouse));
        audited.Should().Be(2);

        using (HttpResponseMessage hidden = await SendAsync(client, HttpMethod.Get, Report, storeManager))
        {
            hidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using HttpResponseMessage badRange = await SendAsync(
            client, HttpMethod.Get, $"{Report}?from=2026-09-14T00:00:00Z&to=2026-09-01T00:00:00Z", owner);
        badRange.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(badRange)).Should().Be("inventory.report_range_invalid");
    }

    private static async Task<Guid> PrepareTransferForDispatchAsync(
        HttpClient client, string requester, string approver, LocationId source, LocationId destination, ProductId product)
    {
        Guid transferId;

        using (HttpResponseMessage created = await SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/transfers",
            requester,
            new
            {
                sourceLocationId = source.Value,
                destinationLocationId = destination.Value,
                lines = new object[] { new { productId = product.Value, quantity = 6m } },
            }))
        {
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
            transferId = (await ReadJsonAsync(created)).RootElement.GetProperty("id").GetGuid();
        }

        await ExpectOkAsync(client, $"/api/v1/transfers/{transferId}/submit", requester, body: null);
        await ExpectOkAsync(client, $"/api/v1/transfers/{transferId}/review", approver, new { note = "Reviewing the request." });
        await ExpectOkAsync(client, $"/api/v1/transfers/{transferId}/approve", approver, new { note = "Approved." });
        await ExpectOkAsync(
            client,
            $"/api/v1/transfers/{transferId}/pick",
            approver,
            new { allocations = new object[] { new { lineNo = 1, batchId = (Guid?)null, quantity = 6m } } });
        await ExpectOkAsync(client, $"/api/v1/transfers/{transferId}/ready", approver, body: null);

        return transferId;
    }

    private static async Task ExpectOkAsync(HttpClient client, string path, string token, object? body)
    {
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, path, token, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Sets a bucket directly, as the transfer tests seed stock: no production flow
    /// yet produces available stock, and draining it stands in for goods that went
    /// missing between pick and dispatch.
    /// </summary>
    private Task<int> SetAvailableAsync(LocationId location, ProductId product, decimal quantity, bool insert)
        => factory.WithServiceAsync(async context =>
        {
            int rows = insert
                ? await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO inventory_balance
                         (location_id, product_id, batch_key, state, quantity,
                          average_unit_cost, total_value, last_movement_id,
                          last_movement_at_utc, version)
                     VALUES
                         ({location.Value}, {product.Value}, {Guid.Empty}, {(short)InventoryState.Available},
                          {quantity}, {95m}, {quantity * 95m}, {Guid.CreateVersion7()},
                          {DateTimeOffset.UtcNow}, {0})
                     """)
                : await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE inventory_balance
                        SET quantity = {quantity}, total_value = {quantity * 95m}
                      WHERE location_id = {location.Value}
                        AND product_id = {product.Value}
                        AND state = {(short)InventoryState.Available}
                     """);

            rows.Should().Be(1);
            return rows;
        });
}
