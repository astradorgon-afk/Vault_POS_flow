using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the balance projection.</summary>
public sealed class InventoryBalanceConfiguration : IEntityTypeConfiguration<InventoryBalance>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<InventoryBalance> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("inventory_balance", PosDbContext.InventorySchema);

        // The batch key substitutes the empty identifier for products that are
        // not batch-tracked, so the primary key stays non-nullable and unique.
        builder.HasKey(b => new { b.LocationId, b.ProductId, b.BatchKey, b.State });

        builder.Property(b => b.LocationId).HasColumnName("location_id");
        builder.Property(b => b.ProductId).HasColumnName("product_id");
        builder.Property(b => b.BatchKey).HasColumnName("batch_key");
        builder.Property(b => b.State).HasColumnName("state").HasConversion<short>();

        builder.Property(b => b.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(b => b.AverageUnitCost)
            .HasColumnName("average_unit_cost")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(b => b.TotalValue)
            .HasColumnName("total_value")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(b => b.LastMovementId).HasColumnName("last_movement_id").IsRequired();
        builder.Property(b => b.LastMovementAtUtc).HasColumnName("last_movement_at_utc").IsRequired();

        builder.HasIndex(b => new { b.ProductId, b.LocationId })
            .HasDatabaseName("ix_inventory_balance_product_location");

        builder.HasIndex(b => new { b.LocationId, b.State })
            .HasDatabaseName("ix_inventory_balance_location_state");
    }
}
