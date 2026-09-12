using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Transfers;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps transfer orders, their lines, picking allocations, discrepancies and custody events.</summary>
public sealed class TransferConfiguration : IEntityTypeConfiguration<Transfer>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Transfer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("transfer_order", PosDbContext.TransfersSchema);

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();

        // A draft has no number; the TRF number is allocated at dispatch so a
        // never-dispatched transfer does not consume a sequence value.
        builder.Property(t => t.Number).HasColumnName("number").HasMaxLength(20).IsRequired();
        builder.Property(t => t.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(t => t.SourceLocationId).HasColumnName("source_location_id").IsRequired();
        builder.Property(t => t.DestinationLocationId).HasColumnName("destination_location_id").IsRequired();
        builder.Property(t => t.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(t => t.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.Property(t => t.ApprovedByUserId).HasColumnName("approved_by_user_id");
        builder.Property(t => t.ApprovedAtUtc).HasColumnName("approved_at_utc");

        builder.Property(t => t.PickedByUserId).HasColumnName("picked_by_user_id");
        builder.Property(t => t.PickedAtUtc).HasColumnName("picked_at_utc");

        builder.Property(t => t.DispatchedByUserId).HasColumnName("dispatched_by_user_id");
        builder.Property(t => t.DispatchedAtUtc).HasColumnName("dispatched_at_utc");
        builder.Property(t => t.ShipmentId).HasColumnName("shipment_id");
        builder.Property(t => t.ShipmentNumber).HasColumnName("shipment_number").HasMaxLength(64);

        builder.Property(t => t.CancelledByUserId).HasColumnName("cancelled_by_user_id");
        builder.Property(t => t.CancelledAtUtc).HasColumnName("cancelled_at_utc");
        builder.Property(t => t.CancelReason).HasColumnName("cancel_reason").HasMaxLength(512);

        builder.Property(t => t.ReceiptId).HasColumnName("receipt_id");
        builder.Property(t => t.ReceiptNumber).HasColumnName("receipt_number").HasMaxLength(64);
        builder.Property(t => t.ReceivedByUserId).HasColumnName("received_by_user_id");
        builder.Property(t => t.ReceivedAtUtc).HasColumnName("received_at_utc");

        builder.Property(t => t.VerifiedByUserId).HasColumnName("verified_by_user_id");
        builder.Property(t => t.VerifiedAtUtc).HasColumnName("verified_at_utc");

        // The empty draft number makes the column non-unique at the database
        // level, so uniqueness is enforced only where the number is populated.
        builder.HasIndex(t => t.Number)
            .IsUnique()
            .HasDatabaseName("ux_transfer_order_number")
            .HasFilter(@"""number"" <> ''");

        builder.HasIndex(t => new { t.SourceLocationId, t.CreatedAtUtc })
            .HasDatabaseName("ix_transfer_order_source_time");
        builder.HasIndex(t => new { t.DestinationLocationId, t.CreatedAtUtc })
            .HasDatabaseName("ix_transfer_order_destination_time");

        builder.HasMany(t => t.Lines)
            .WithOne()
            .HasForeignKey(l => l.TransferOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Allocations)
            .WithOne()
            .HasForeignKey(a => a.TransferOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Discrepancies)
            .WithOne()
            .HasForeignKey(d => d.TransferOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.CustodyEvents)
            .WithOne()
            .HasForeignKey(e => e.TransferOrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps one requested line of a transfer order.</summary>
public sealed class TransferLineConfiguration : IEntityTypeConfiguration<TransferLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TransferLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("transfer_order_line", PosDbContext.TransfersSchema);

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(l => l.TransferOrderId).HasColumnName("transfer_order_id").IsRequired();
        builder.Property(l => l.LineNo).HasColumnName("line_no").IsRequired();
        builder.Property(l => l.ProductId).HasColumnName("product_id").IsRequired();

        builder.Property(l => l.RequestedQuantity)
            .HasColumnName("requested_quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(l => l.Note).HasColumnName("note").HasMaxLength(512);

        builder.HasIndex(l => new { l.TransferOrderId, l.LineNo })
            .IsUnique()
            .HasDatabaseName("ux_transfer_order_line_no");
    }
}

/// <summary>Maps a picked quantity of a concrete lot, snapped at pick time.</summary>
public sealed class TransferAllocationConfiguration : IEntityTypeConfiguration<TransferPickAllocation>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TransferPickAllocation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("transfer_allocation", PosDbContext.TransfersSchema);

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(a => a.TransferOrderId).HasColumnName("transfer_order_id").IsRequired();
        builder.Property(a => a.LineNo).HasColumnName("line_no").IsRequired();
        builder.Property(a => a.BatchId).HasColumnName("batch_id");

        builder.Property(a => a.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(a => a.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(a => a.ReceivedQuantity)
            .HasColumnName("received_quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(a => a.DamagedQuantity)
            .HasColumnName("damaged_quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.HasIndex(a => new { a.TransferOrderId, a.LineNo })
            .HasDatabaseName("ix_transfer_allocation_order_line");
    }
}

/// <summary>Maps an arrival shortage recorded against a transfer.</summary>
public sealed class TransferDiscrepancyConfiguration : IEntityTypeConfiguration<TransferDiscrepancy>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TransferDiscrepancy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("transfer_discrepancy", PosDbContext.TransfersSchema);

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(d => d.TransferOrderId).HasColumnName("transfer_order_id").IsRequired();
        builder.Property(d => d.LineNo).HasColumnName("line_no").IsRequired();
        builder.Property(d => d.BatchId).HasColumnName("batch_id");
        builder.Property(d => d.Kind).HasColumnName("kind").HasConversion<short>().IsRequired();

        builder.Property(d => d.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(d => d.ResolutionOutcome).HasColumnName("resolution_outcome").HasConversion<short>();
        builder.Property(d => d.ResolvedByUserId).HasColumnName("resolved_by_user_id");
        builder.Property(d => d.ResolvedAtUtc).HasColumnName("resolved_at_utc");
        builder.Property(d => d.ResolutionNote).HasColumnName("resolution_note").HasMaxLength(512);

        builder.HasIndex(d => new { d.TransferOrderId, d.LineNo })
            .HasDatabaseName("ix_transfer_discrepancy_order_line");
    }
}

/// <summary>Maps one step in a transfer's custody timeline.</summary>
public sealed class TransferCustodyEventConfiguration : IEntityTypeConfiguration<TransferCustodyEvent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TransferCustodyEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("transfer_custody_event", PosDbContext.TransfersSchema);

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TransferOrderId).HasColumnName("transfer_order_id").IsRequired();
        builder.Property(e => e.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(e => e.Kind).HasColumnName("kind").HasConversion<short>().IsRequired();
        builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(e => e.Note).HasColumnName("note").HasMaxLength(512);

        builder.HasIndex(e => new { e.TransferOrderId, e.Sequence })
            .IsUnique()
            .HasDatabaseName("ux_transfer_custody_sequence");
    }
}