using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps purchase orders, their lines and their approval decisions.</summary>
public sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("purchase_order", PosDbContext.PurchasingSchema);

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();

        // A draft has no number; the number is allocated at submission so that
        // cancelled drafts never consume a sequence value.
        builder.Property(o => o.Number).HasColumnName("number").HasMaxLength(20);
        builder.Property(o => o.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(o => o.SupplierId).HasColumnName("supplier_id").IsRequired();
        builder.Property(o => o.DestinationLocationId).HasColumnName("destination_location_id").IsRequired();
        builder.Property(o => o.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();

        builder.Property(o => o.Subtotal)
            .HasColumnName("subtotal")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();
        builder.Property(o => o.TaxTotal)
            .HasColumnName("tax_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();
        builder.Property(o => o.GrandTotal)
            .HasColumnName("grand_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(o => o.OrderedAtUtc).HasColumnName("ordered_at_utc");
        builder.Property(o => o.ExpectedAtUtc).HasColumnName("expected_at_utc");
        builder.Property(o => o.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(o => o.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(o => o.CancelledReason).HasColumnName("cancelled_reason").HasMaxLength(512);
        builder.Property(o => o.ClosedReason).HasColumnName("closed_reason").HasMaxLength(512);

        builder.HasIndex(o => o.Number).IsUnique().HasDatabaseName("ux_purchase_order_number");

        builder.HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey(l => l.PurchaseOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(o => o.Approvals)
            .WithOne()
            .HasForeignKey(a => a.PurchaseOrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps a purchase order line.</summary>
public sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("purchase_order_line", PosDbContext.PurchasingSchema);

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(l => l.PurchaseOrderId).HasColumnName("purchase_order_id").IsRequired();
        builder.Property(l => l.LineNo).HasColumnName("line_no").IsRequired();
        builder.Property(l => l.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(l => l.UnitOfMeasureId).HasColumnName("uom_id").IsRequired();

        builder.Property(l => l.OrderedQuantity)
            .HasColumnName("ordered_quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(l => l.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(l => l.LineTotal)
            .HasColumnName("line_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.HasIndex(l => new { l.PurchaseOrderId, l.LineNo })
            .IsUnique()
            .HasDatabaseName("ux_purchase_order_line_no");
    }
}

/// <summary>Maps a purchase order approval decision.</summary>
public sealed class PurchaseApprovalConfiguration : IEntityTypeConfiguration<PurchaseApproval>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PurchaseApproval> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("purchase_approval", PosDbContext.PurchasingSchema);

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(a => a.PurchaseOrderId).HasColumnName("purchase_order_id").IsRequired();
        builder.Property(a => a.ApproverUserId).HasColumnName("approver_user_id").IsRequired();
        builder.Property(a => a.Decision).HasColumnName("decision").HasConversion<short>().IsRequired();
        builder.Property(a => a.DecidedAtUtc).HasColumnName("decided_at_utc").IsRequired();
        builder.Property(a => a.Notes).HasColumnName("notes").HasMaxLength(512);

        builder.Property(a => a.ThresholdApplied)
            .HasColumnName("threshold_applied")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.HasIndex(a => new { a.PurchaseOrderId, a.DecidedAtUtc })
            .HasDatabaseName("ix_purchase_approval_order_time");
    }
}

/// <summary>Maps the central document counter rows.</summary>
public sealed class DocumentCounterConfiguration : IEntityTypeConfiguration<DocumentCounter>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentCounter> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("document_counter", PosDbContext.CoreSchema);

        builder.HasKey(c => new { c.DocumentType, c.PeriodKey, c.ScopeKey });

        builder.Property(c => c.DocumentType).HasColumnName("document_type").HasConversion<short>().IsRequired();
        builder.Property(c => c.PeriodKey).HasColumnName("period_key").HasMaxLength(7).IsRequired();
        builder.Property(c => c.ScopeKey).HasColumnName("scope_key").HasMaxLength(8).IsRequired();
        builder.Property(c => c.NextValue).HasColumnName("next_value").IsRequired();
    }
}