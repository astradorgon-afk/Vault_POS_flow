using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps a stock adjustment.</summary>
public sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<StockAdjustment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("stock_adjustment", PosDbContext.InventorySchema);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        // Unnumbered until posted, so a rejected adjustment does not consume a number.
        builder.Property(a => a.Number).HasColumnName("number").HasMaxLength(DocumentNumber.MaxLength).IsRequired();
        builder.Property(a => a.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(a => a.Reason).HasColumnName("reason").HasConversion<short>().IsRequired();
        builder.Property(a => a.Notes).HasColumnName("notes").HasMaxLength(StockAdjustment.NotesMaxLength);
        builder.Property(a => a.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(a => a.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(a => a.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(a => a.SubmittedAtUtc).HasColumnName("submitted_at_utc");
        builder.Property(a => a.DecidedByUserId).HasColumnName("decided_by_user_id");
        builder.Property(a => a.DecidedAtUtc).HasColumnName("decided_at_utc");
        builder.Property(a => a.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(512);
        builder.Property(a => a.ReversedByUserId).HasColumnName("reversed_by_user_id");
        builder.Property(a => a.ReversedAtUtc).HasColumnName("reversed_at_utc");
        builder.Property(a => a.ReversalReason).HasColumnName("reversal_reason").HasMaxLength(512);

        builder.Ignore(a => a.TotalAbsoluteValue);

        builder.HasIndex(a => a.Number)
            .IsUnique()
            .HasDatabaseName("ux_stock_adjustment_number")
            .HasFilter(@"""number"" <> ''");
        builder.HasIndex(a => new { a.LocationId, a.CreatedAtUtc }).HasDatabaseName("ix_stock_adjustment_location_time");

        builder.HasMany(a => a.Lines)
            .WithOne()
            .HasForeignKey(l => l.StockAdjustmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps a stock adjustment line.</summary>
public sealed class StockAdjustmentLineConfiguration : IEntityTypeConfiguration<StockAdjustmentLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<StockAdjustmentLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("stock_adjustment_line", PosDbContext.InventorySchema);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(l => l.StockAdjustmentId).HasColumnName("stock_adjustment_id").IsRequired();
        builder.Property(l => l.LineNo).HasColumnName("line_no").IsRequired();
        builder.Property(l => l.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(l => l.BatchId).HasColumnName("batch_id");
        builder.Property(l => l.State).HasColumnName("state").HasConversion<short>().IsRequired();
        builder.Property(l => l.QuantityDelta).HasColumnName("quantity_delta").HasPrecision(18, Quantity.Scale).IsRequired();
        builder.Property(l => l.UnitCost).HasColumnName("unit_cost").HasPrecision(19, Money.StorageScale).IsRequired();
        builder.Property(l => l.MovementType).HasColumnName("movement_type").HasConversion<short>().IsRequired();

        builder.Ignore(l => l.AbsoluteValue);

        builder.HasIndex(l => new { l.StockAdjustmentId, l.LineNo }).IsUnique().HasDatabaseName("ux_stock_adjustment_line_no");
    }
}

/// <summary>Maps an inventory count.</summary>
public sealed class InventoryCountConfiguration : IEntityTypeConfiguration<InventoryCount>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<InventoryCount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("inventory_count", PosDbContext.InventorySchema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.Number).HasColumnName("number").HasMaxLength(DocumentNumber.MaxLength).IsRequired();
        builder.Property(c => c.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(c => c.Kind).HasColumnName("kind").HasConversion<short>().IsRequired();
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(c => c.Note).HasColumnName("note").HasMaxLength(512);
        builder.Property(c => c.SnapshotTakenAtUtc).HasColumnName("snapshot_taken_at_utc").IsRequired();
        builder.Property(c => c.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.SubmittedByUserId).HasColumnName("submitted_by_user_id");
        builder.Property(c => c.SubmittedAtUtc).HasColumnName("submitted_at_utc");
        builder.Property(c => c.ApprovedByUserId).HasColumnName("approved_by_user_id");
        builder.Property(c => c.PostedAtUtc).HasColumnName("posted_at_utc");
        builder.Property(c => c.LastRejectionReason).HasColumnName("last_rejection_reason").HasMaxLength(512);
        builder.Property(c => c.CancellationReason).HasColumnName("cancellation_reason").HasMaxLength(512);

        builder.Ignore(c => c.TotalAbsoluteVarianceValue);

        builder.HasIndex(c => c.Number).IsUnique().HasDatabaseName("ux_inventory_count_number");
        builder.HasIndex(c => new { c.LocationId, c.CreatedAtUtc }).HasDatabaseName("ix_inventory_count_location_time");

        builder.HasMany(c => c.Lines)
            .WithOne()
            .HasForeignKey(l => l.InventoryCountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps an inventory count line.</summary>
public sealed class InventoryCountLineConfiguration : IEntityTypeConfiguration<InventoryCountLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<InventoryCountLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("inventory_count_line", PosDbContext.InventorySchema);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(l => l.InventoryCountId).HasColumnName("inventory_count_id").IsRequired();
        builder.Property(l => l.LineNo).HasColumnName("line_no").IsRequired();
        builder.Property(l => l.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(l => l.BatchId).HasColumnName("batch_id");
        builder.Property(l => l.SystemQuantity).HasColumnName("system_quantity").HasPrecision(18, Quantity.Scale).IsRequired();
        builder.Property(l => l.PhysicalQuantity).HasColumnName("physical_quantity").HasPrecision(18, Quantity.Scale);
        builder.Property(l => l.UnitCost).HasColumnName("unit_cost").HasPrecision(19, Money.StorageScale).IsRequired();
        builder.Property(l => l.CountedByUserId).HasColumnName("counted_by_user_id");
        builder.Property(l => l.CountedAtUtc).HasColumnName("counted_at_utc");
        builder.Property(l => l.IsRepeatVariance).HasColumnName("is_repeat_variance").IsRequired();

        builder.Ignore(l => l.Variance);
        builder.Ignore(l => l.VarianceValue);

        builder.HasIndex(l => new { l.InventoryCountId, l.LineNo }).IsUnique().HasDatabaseName("ux_inventory_count_line_no");
        builder.HasIndex(l => l.ProductId).HasDatabaseName("ix_inventory_count_line_product");
    }
}
