using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Stock set-up and read-back for the inventory control tests. No production flow
/// yet produces available stock (receipts arrive in pending inspection), so buckets
/// are seeded directly, the same way the transfer tests do.
/// </summary>
internal static class InventoryControlTestSupport
{
    /// <summary>Inserts a bucket, or overwrites its quantity when it exists.</summary>
    internal static Task SetBucketAsync(
        PosApiFactory factory,
        LocationId location,
        ProductId product,
        decimal quantity,
        decimal unitCost,
        InventoryState state = InventoryState.Available)
        => factory.WithServiceAsync(async context =>
        {
            int updated = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE inventory_balance
                    SET quantity = {quantity}, average_unit_cost = {unitCost}, total_value = {quantity * unitCost}
                  WHERE location_id = {location.Value} AND product_id = {product.Value}
                    AND batch_key = {Guid.Empty} AND state = {(short)state}
                 """);

            if (updated == 0)
            {
                updated = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO inventory_balance
                         (location_id, product_id, batch_key, state, quantity, average_unit_cost, total_value,
                          last_movement_id, last_movement_at_utc, version)
                     VALUES
                         ({location.Value}, {product.Value}, {Guid.Empty}, {(short)state}, {quantity}, {unitCost},
                          {quantity * unitCost}, {Guid.CreateVersion7()}, {DateTimeOffset.UtcNow}, {0})
                     """);
            }

            updated.Should().Be(1);
            return updated;
        });

    /// <summary>Reads a bucket's quantity; zero when it does not exist.</summary>
    internal static Task<decimal> QuantityAsync(
        PosApiFactory factory,
        LocationId location,
        ProductId product,
        InventoryState state = InventoryState.Available)
        => factory.WithServiceAsync(async context =>
        {
            List<InventoryBalance> buckets = await context.InventoryBalances
                .AsNoTracking()
                .Where(b => b.LocationId == location && b.ProductId == product && b.State == state)
                .ToListAsync();

            return buckets.Sum(b => b.Quantity);
        });

    /// <summary>Reads the ledger legs posted under a document.</summary>
    internal static Task<List<InventoryMovement>> MovementsAsync(PosApiFactory factory, Guid documentId)
        => factory.WithServiceAsync(context => context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ReferenceDocumentId == documentId)
            .ToListAsync());
}
