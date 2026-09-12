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

/// <summary>Maps a goods receipt: the physical delivery against a purchase order.</summary>
public sealed class GoodsReceiptConfiguration : IEntityTypeConfiguration<GoodsReceipt>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<GoodsReceipt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("goods_receipt", PosDbContext.PurchasingSchema);

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.Number).HasColumnName("number").HasMaxLength(20).IsRequired();
        builder.Property(r => r.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(r => r.PurchaseOrderId).HasColumnName("purchase_order_id").IsRequired();
        builder.Property(r => r.SupplierId).HasColumnName("supplier_id").IsRequired();
        builder.Property(r => r.DestinationLocationId).HasColumnName("destination_location_id").IsRequired();
        builder.Property(r => r.DocumentsMissing).HasColumnName("documents_missing").IsRequired();
        builder.Property(r => r.BusinessDate).HasColumnName("business_date").IsRequired();
        builder.Property(r => r.ReceivedAtUtc).HasColumnName("received_at_utc").IsRequired();
        builder.Property(r => r.ReceivedByUserId).HasColumnName("received_by_user_id").IsRequired();
        builder.Property(r => r.CostVariancePendingApproval).HasColumnName("cost_variance_pending_approval").IsRequired();

        builder.Property(r => r.CostVarianceValueAtStake)
            .HasColumnName("cost_variance_value_at_stake")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.HasIndex(r => r.Number).IsUnique().HasDatabaseName("ux_goods_receipt_number");

        builder.HasIndex(r => new { r.PurchaseOrderId, r.ReceivedAtUtc })
            .HasDatabaseName("ix_goods_receipt_order_time");

        builder.HasMany(r => r.Lines)
            .WithOne()
            .HasForeignKey(l => l.GoodsReceiptId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.Discrepancies)
            .WithOne()
            .HasForeignKey(d => d.GoodsReceiptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps a goods receipt line with its disposition plan.</summary>
public sealed class GoodsReceiptLineConfiguration : IEntityTypeConfiguration<GoodsReceiptLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<GoodsReceiptLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("goods_receipt_line", PosDbContext.PurchasingSchema);

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(l => l.GoodsReceiptId).HasColumnName("goods_receipt_id").IsRequired();
        builder.Property(l => l.LineNo).HasColumnName("line_no").IsRequired();
        builder.Property(l => l.PurchaseOrderLineId).HasColumnName("purchase_order_line_id").IsRequired();
        builder.Property(l => l.ProductId).HasColumnName("product_id").IsRequired();

        builder.Property(l => l.QuantityExpected)
            .HasColumnName("quantity_expected")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();
        builder.Property(l => l.QuantityReceived)
            .HasColumnName("quantity_received")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();
        builder.Property(l => l.QuantityDamaged)
            .HasColumnName("quantity_damaged")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();
        builder.Property(l => l.QuantityWrongItem)
            .HasColumnName("quantity_wrong_item")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();
        builder.Property(l => l.QuantityExpired)
            .HasColumnName("quantity_expired")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();
        builder.Property(l => l.OverageBeyondTolerance)
            .HasColumnName("overage_beyond_tolerance")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();
        builder.Property(l => l.QuantityAccepted)
            .HasColumnName("quantity_accepted")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();
        builder.Property(l => l.AcceptedState).HasColumnName("accepted_state").HasConversion<short>().IsRequired();

        builder.Property(l => l.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(l => l.LotNumber).HasColumnName("lot_number").HasMaxLength(64);
        builder.Property(l => l.ManufacturedOn).HasColumnName("manufactured_on");
        builder.Property(l => l.ExpiresOn).HasColumnName("expires_on");

        builder.Property(l => l.CostVariancePercent)
            .HasColumnName("cost_variance_percent")
            .HasPrecision(19, 4)
            .IsRequired();
        builder.Property(l => l.CostVarianceApprovedByUserId).HasColumnName("cost_variance_approved_by_user_id");
        builder.Property(l => l.CostVarianceApprovedAtUtc).HasColumnName("cost_variance_approved_at_utc");

        builder.HasIndex(l => new { l.GoodsReceiptId, l.LineNo })
            .IsUnique()
            .HasDatabaseName("ux_goods_receipt_line_no");
    }
}

/// <summary>Maps a receiving discrepancy recorded against a receipt.</summary>
public sealed class ReceivingDiscrepancyConfiguration : IEntityTypeConfiguration<ReceivingDiscrepancy>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReceivingDiscrepancy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("receiving_discrepancy", PosDbContext.PurchasingSchema);

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(d => d.GoodsReceiptId).HasColumnName("goods_receipt_id").IsRequired();
        builder.Property(d => d.PurchaseOrderLineId).HasColumnName("purchase_order_line_id").IsRequired();
        builder.Property(d => d.LineNo).HasColumnName("line_no").IsRequired();
        builder.Property(d => d.Kind).HasColumnName("kind").HasConversion<short>().IsRequired();

        builder.Property(d => d.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(d => d.ValueImpact)
            .HasColumnName("value_impact")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(d => d.ResolutionOutcome).HasColumnName("resolution_outcome").HasConversion<short>();
        builder.Property(d => d.ResolutionNote).HasColumnName("resolution_note").HasMaxLength(512);
        builder.Property(d => d.ResolvedByUserId).HasColumnName("resolved_by_user_id");
        builder.Property(d => d.ResolvedAtUtc).HasColumnName("resolved_at_utc");
    }
}

/// <summary>Maps a direct-to-store delivery authorization.</summary>
public sealed class DirectDeliveryAuthorizationConfiguration : IEntityTypeConfiguration<DirectDeliveryAuthorization>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DirectDeliveryAuthorization> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("direct_delivery_authorization", PosDbContext.PurchasingSchema);

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(a => a.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(a => a.SupplierId).HasColumnName("supplier_id").IsRequired();
        builder.Property(a => a.StoreLocationId).HasColumnName("store_location_id").IsRequired();
        builder.Property(a => a.ValidFrom).HasColumnName("valid_from").IsRequired();
        builder.Property(a => a.ValidUntil).HasColumnName("valid_until").IsRequired();
        builder.Property(a => a.ProductId).HasColumnName("product_id");
        builder.Property(a => a.ValueCap).HasColumnName("value_cap").HasPrecision(19, Money.StorageScale);

        builder.Property(a => a.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(a => a.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(a => a.RevokedByUserId).HasColumnName("revoked_by_user_id");
        builder.Property(a => a.RevokedAtUtc).HasColumnName("revoked_at_utc");

        // The receiving path looks authorizations up by supplier, store and
        // window, so the index mirrors that lookup.
        builder.HasIndex(a => new { a.SupplierId, a.StoreLocationId, a.ValidFrom, a.ValidUntil })
            .HasDatabaseName("ix_dda_supplier_store_window");
    }
}

/// <summary>Maps a supplier return document.</summary>
public sealed class SupplierReturnConfiguration : IEntityTypeConfiguration<SupplierReturn>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SupplierReturn> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("supplier_return", PosDbContext.PurchasingSchema);

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        // A draft has no number; the SRT number is allocated at dispatch so a
        // never-dispatched return does not consume a sequence value.
        builder.Property(r => r.Number).HasColumnName("number").HasMaxLength(20).IsRequired();
        builder.Property(r => r.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(r => r.SupplierId).HasColumnName("supplier_id").IsRequired();
        builder.Property(r => r.LocationId).HasColumnName("location_id").IsRequired();

        builder.Property(r => r.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(r => r.ApprovedByUserId).HasColumnName("approved_by_user_id");
        builder.Property(r => r.ApprovedAtUtc).HasColumnName("approved_at_utc");
        builder.Property(r => r.SupplierAuthorizationNumber).HasColumnName("supplier_authorization_number").HasMaxLength(64);
        builder.Property(r => r.DispatchedAtUtc).HasColumnName("dispatched_at_utc");
        builder.Property(r => r.ConfirmedAtUtc).HasColumnName("confirmed_at_utc");

        builder.HasIndex(r => r.Number)
                .IsUnique()
                .HasDatabaseName("ux_supplier_return_number")
                .HasFilter(@"""number"" <> ''");
        builder.HasIndex(r => new { r.LocationId, r.CreatedAtUtc })
            .HasDatabaseName("ix_supplier_return_location_time");

        builder.HasMany(r => r.Lines)
            .WithOne()
            .HasForeignKey(l => l.SupplierReturnId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps a supplier return line.</summary>
public sealed class SupplierReturnLineConfiguration : IEntityTypeConfiguration<SupplierReturnLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SupplierReturnLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("supplier_return_line", PosDbContext.PurchasingSchema);

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(l => l.SupplierReturnId).HasColumnName("supplier_return_id").IsRequired();
        builder.Property(l => l.LineNo).HasColumnName("line_no").IsRequired();
        builder.Property(l => l.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(l => l.BatchId).HasColumnName("batch_id");
        builder.Property(l => l.SourceState).HasColumnName("source_state").HasConversion<short>().IsRequired();

        builder.Property(l => l.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(l => l.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(l => l.Reason).HasColumnName("reason").HasConversion<short>().IsRequired();
        builder.Property(l => l.Notes).HasColumnName("notes").HasMaxLength(512);

        builder.HasIndex(l => new { l.SupplierReturnId, l.LineNo })
            .IsUnique()
            .HasDatabaseName("ux_supplier_return_line_no");
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