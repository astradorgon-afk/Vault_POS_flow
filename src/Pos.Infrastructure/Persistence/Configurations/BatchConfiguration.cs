using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps a received lot of a batch-tracked product. The batch identifier
/// travels with the movement legs (which deliberately carry no foreign key to
/// this table, so a movement is never blocked by a detached reference); this
/// row is the source of truth for the lot's origin, cost and dates.
/// </summary>
public sealed class BatchConfiguration : IEntityTypeConfiguration<Batch>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Batch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("batch", PosDbContext.InventorySchema);

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(b => b.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(b => b.SupplierId).HasColumnName("supplier_id").IsRequired();
        builder.Property(b => b.LotNumber).HasColumnName("lot_number").HasMaxLength(64).IsRequired();
        builder.Property(b => b.ReceivedOn).HasColumnName("received_on").IsRequired();
        builder.Property(b => b.ManufacturedOn).HasColumnName("manufactured_on");
        builder.Property(b => b.ExpiresOn).HasColumnName("expires_on");

        builder.Property(b => b.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(b => b.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(b => b.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasIndex(b => new { b.ProductId, b.LotNumber })
            .IsUnique()
            .HasDatabaseName("ux_batch_product_lot");
    }
}