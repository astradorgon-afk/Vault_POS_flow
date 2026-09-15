using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the customer return aggregate, its lines, and its refunds.</summary>
public sealed class SalesReturnConfiguration : IEntityTypeConfiguration<SalesReturn>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SalesReturn> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sales_return", PosDbContext.SalesSchema);

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        // The RET number is device-allocated; the column is unique because a
        // replayed acceptance must return the original document.
        builder.Property(r => r.Number)
            .HasColumnName("number")
            .HasMaxLength(DocumentNumber.MaxLength)
            .IsRequired();

        builder.Property(r => r.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(r => r.SaleId).HasColumnName("sale_id");
        builder.Property(r => r.IsBlind).HasColumnName("is_blind").IsRequired();
        builder.Property(r => r.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(r => r.CashierShiftId).HasColumnName("cashier_shift_id").IsRequired();
        builder.Property(r => r.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(r => r.CustomerId).HasColumnName("customer_id");
        builder.Property(r => r.BusinessDate).HasColumnName("business_date").IsRequired();
        builder.Property(r => r.ReturnedAtUtc).HasColumnName("returned_at_utc").IsRequired();
        builder.Property(r => r.ReturnedByUserId).HasColumnName("returned_by_user_id").IsRequired();

        builder.Property(r => r.RefundableTotal)
            .HasColumnName("refundable_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        // The event id makes retried acceptances idempotent, exactly as the
        // sale's event id does.
        builder.HasIndex(r => r.EventId)
            .IsUnique()
            .HasDatabaseName("ux_sales_return_event_id");

        builder.HasIndex(r => r.Number)
            .IsUnique()
            .HasDatabaseName("ux_sales_return_number");

        builder.HasIndex(r => new { r.LocationId, r.BusinessDate })
            .HasDatabaseName("ix_sales_return_location_business_date");

        builder.HasIndex(r => new { r.SaleId, r.Id })
            .HasDatabaseName("ix_sales_return_sale");

        builder.HasMany(r => r.Items)
            .WithOne()
            .HasForeignKey(i => i.SalesReturnId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.Refunds)
            .WithOne()
            .HasForeignKey(f => f.SalesReturnId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps one proportional snapshot line of a customer return.</summary>
public sealed class SalesReturnItemConfiguration : IEntityTypeConfiguration<SalesReturnItem>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SalesReturnItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sales_return_item", PosDbContext.SalesSchema);

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(i => i.SalesReturnId).HasColumnName("sales_return_id").IsRequired();
        builder.Property(i => i.LineNumber).HasColumnName("line_no").IsRequired();
        builder.Property(i => i.SaleItemId).HasColumnName("sale_item_id");
        builder.Property(i => i.ProductId).HasColumnName("product_id").IsRequired();

        builder.Property(i => i.ProductName)
            .HasColumnName("product_name")
            .HasMaxLength(SalesReturnItem.ProductNameMaxLength)
            .IsRequired();

        builder.Property(i => i.Barcode)
            .HasColumnName("barcode")
            .HasMaxLength(SalesReturnItem.BarcodeMaxLength);

        builder.Property(i => i.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(i => i.UnitOfMeasureId).HasColumnName("uom_id").IsRequired();

        builder.Property(i => i.UnitPrice)
            .HasColumnName("unit_price")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.GrossAmount)
            .HasColumnName("gross_amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.Discount)
            .HasColumnName("discount_amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.NetAmount)
            .HasColumnName("net_amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.RefundableAmount)
            .HasColumnName("refundable_amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.VatRate)
            .HasColumnName("vat_rate")
            .HasPrecision(19, Money.StorageScale);

        builder.Property(i => i.IsVatExempt).HasColumnName("is_vat_exempt").IsRequired();
        builder.Property(i => i.IsZeroRated).HasColumnName("is_zero_rated").IsRequired();

        builder.Property(i => i.VatBase)
            .HasColumnName("vat_base")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.Vat)
            .HasColumnName("vat_amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.BatchId).HasColumnName("batch_id");
        builder.Property(i => i.BatchCode).HasColumnName("batch_code").HasMaxLength(Batch.LotNumberMaxLength);
        builder.Property(i => i.BatchExpiresOn).HasColumnName("batch_expires_on");

        builder.Property(i => i.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.HasIndex(i => new { i.SalesReturnId, i.LineNumber })
            .IsUnique()
            .HasDatabaseName("ux_sales_return_item_line_no");

        builder.HasIndex(i => new { i.ProductId, i.SalesReturnId })
            .HasDatabaseName("ix_sales_return_item_product");

        builder.HasIndex(i => i.SaleItemId)
            .HasDatabaseName("ix_sales_return_item_sale_item");
    }
}

/// <summary>Maps one refund issued against a customer return.</summary>
public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("refund", PosDbContext.SalesSchema);

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(f => f.SalesReturnId).HasColumnName("sales_return_id").IsRequired();
        builder.Property(f => f.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(f => f.CashierShiftId).HasColumnName("cashier_shift_id").IsRequired();
        builder.Property(f => f.DeviceId).HasColumnName("device_id").IsRequired();

        builder.Property(f => f.Method).HasColumnName("method").HasConversion<short>().IsRequired();

        builder.Property(f => f.Amount)
            .HasColumnName("amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(f => f.Tendered)
            .HasColumnName("tendered")
            .HasPrecision(19, Money.StorageScale);

        builder.Property(f => f.ProviderReference)
            .HasColumnName("provider_reference")
            .HasMaxLength(Refund.ProviderReferenceMaxLength);

        builder.Property(f => f.RefundedAtUtc).HasColumnName("refunded_at_utc").IsRequired();
        builder.Property(f => f.RefundedByUserId).HasColumnName("refunded_by_user_id").IsRequired();

        // The event id makes retried refunds idempotent; the unique index is the
        // database-level tripwire that a second refund with the same event
        // cannot slip through.
        builder.HasIndex(f => f.EventId)
            .IsUnique()
            .HasDatabaseName("ux_refund_event_id");

        builder.HasIndex(f => new { f.SalesReturnId, f.Id })
            .HasDatabaseName("ix_refund_return");

        builder.HasIndex(f => new { f.CashierShiftId, f.Method })
            .HasDatabaseName("ix_refund_shift_method");
    }
}