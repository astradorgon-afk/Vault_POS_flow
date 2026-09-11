using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the append-only inventory ledger.</summary>
public sealed class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("inventory_movement", PosDbContext.InventorySchema);

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(m => m.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(m => m.MovementGroupId).HasColumnName("movement_group_id").IsRequired();
        builder.Property(m => m.LegNumber).HasColumnName("leg_number").IsRequired();

        builder.Property(m => m.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(m => m.BatchId).HasColumnName("batch_id");
        builder.Property(m => m.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(m => m.State).HasColumnName("state").HasConversion<short>().IsRequired();

        builder.Property(m => m.QuantityDelta)
            .HasColumnName("quantity_delta")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(m => m.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(m => m.TotalValueDelta)
            .HasColumnName("total_value_delta")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(m => m.MovementType).HasColumnName("movement_type").HasConversion<short>().IsRequired();

        builder.Property(m => m.SourceLocationId).HasColumnName("source_location_id");
        builder.Property(m => m.DestinationLocationId).HasColumnName("destination_location_id");
        builder.Property(m => m.SourceState).HasColumnName("source_state").HasConversion<short?>();
        builder.Property(m => m.DestinationState).HasColumnName("destination_state").HasConversion<short?>();

        builder.Property(m => m.ReferenceDocumentType)
            .HasColumnName("reference_document_type")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(m => m.ReferenceDocumentId).HasColumnName("reference_document_id");

        builder.Property(m => m.ReferenceNumber)
            .HasColumnName("reference_number")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(m => m.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(m => m.ApprovedByUserId).HasColumnName("approved_by_user_id");
        builder.Property(m => m.DeviceId).HasColumnName("device_id");

        builder.Property(m => m.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(m => m.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();
        builder.Property(m => m.BusinessDate).HasColumnName("business_date").IsRequired();

        builder.Property(m => m.ReasonCode).HasColumnName("reason_code").HasConversion<short?>();
        builder.Property(m => m.Notes).HasColumnName("notes").HasMaxLength(1024);
        builder.Property(m => m.ReversesMovementGroupId).HasColumnName("reverses_movement_group_id");

        builder.Property(m => m.SyncStatus).HasColumnName("sync_status").HasConversion<short>().IsRequired();
        builder.Property(m => m.ServerProcessingStatus)
            .HasColumnName("server_processing_status")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(m => m.CorrelationId).HasColumnName("correlation_id").IsRequired();

        // BatchKey is derived; it exists only for the balance projection join and
        // is never stored.
        builder.Ignore(m => m.BatchKey);
        builder.Ignore(m => m.IsIncrease);

        // A leg number is unique within its group: this is what makes a
        // duplicated insert of the same event fail loudly rather than double-post.
        builder.HasIndex(m => new { m.MovementGroupId, m.LegNumber })
            .IsUnique()
            .HasDatabaseName("ux_inventory_movement_group_leg");

        builder.HasIndex(m => new { m.LocationId, m.ProductId, m.BatchId, m.State, m.RecordedAtUtc })
            .HasDatabaseName("ix_inventory_movement_bucket");

        builder.HasIndex(m => new { m.ProductId, m.RecordedAtUtc })
            .HasDatabaseName("ix_inventory_movement_product_time");

        builder.HasIndex(m => new { m.ReferenceDocumentType, m.ReferenceDocumentId })
            .HasDatabaseName("ix_inventory_movement_reference");

        builder.HasIndex(m => m.EventId).HasDatabaseName("ix_inventory_movement_event");

        builder.HasIndex(m => m.MovementGroupId).HasDatabaseName("ix_inventory_movement_group");

        // Movements are never loaded for modification. No-tracking by default
        // removes the possibility of a stray SaveChanges writing one back.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_inventory_movement_quantity_nonzero", "quantity_delta <> 0");
            t.HasCheckConstraint("ck_inventory_movement_cost_nonnegative", "unit_cost >= 0");
            t.HasCheckConstraint("ck_inventory_movement_leg_positive", "leg_number >= 1");
        });
    }
}
