using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the sales aggregate, its lines, and its payments.</summary>
public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sale", PosDbContext.SalesSchema);

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        // The SAL number is allocated by the caller inside the same transaction
        // as the sale, so the counter advances exactly once; the column is unique
        // because a replayed completion must return the original document.
        builder.Property(s => s.Number)
            .HasColumnName("number")
            .HasMaxLength(DocumentNumber.MaxLength)
            .IsRequired();

        builder.Property(s => s.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(s => s.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(s => s.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(s => s.CashierShiftId).HasColumnName("cashier_shift_id").IsRequired();
        builder.Property(s => s.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(s => s.CustomerId).HasColumnName("customer_id");
        builder.HasOne<Customer>().WithMany().HasForeignKey(s => s.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(s => s.BusinessDate).HasColumnName("business_date").IsRequired();
        builder.Property(s => s.CompletedAtUtc).HasColumnName("completed_at_utc").IsRequired();
        builder.Property(s => s.CompletedByUserId).HasColumnName("completed_by_user_id").IsRequired();

        builder.Property(s => s.VoidedAtUtc).HasColumnName("voided_at_utc");
        builder.Property(s => s.VoidedByUserId).HasColumnName("voided_by_user_id");
        builder.Property(s => s.VoidReason).HasColumnName("void_reason").HasMaxLength(Sale.VoidReasonMaxLength);

        // All totals are money at the storage scale; the convention-wide decimal
        // precision below is the backstop, the explicit declarations document it.
        builder.Property(s => s.GrossTotal)
            .HasColumnName("gross_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();
        builder.Property(s => s.DiscountTotal)
            .HasColumnName("discount_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();
        builder.Property(s => s.NetTotal)
            .HasColumnName("net_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();
        builder.Property(s => s.VatTotal)
            .HasColumnName("vat_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();
        builder.Property(s => s.VatExemptTotal)
            .HasColumnName("vat_exempt_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();
        builder.Property(s => s.ZeroRatedTotal)
            .HasColumnName("zero_rated_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();
        builder.Property(s => s.TaxableBaseTotal)
            .HasColumnName("taxable_base_total")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        // The event id makes retried completions idempotent; the unique index is
        // the database-level tripwire that a second write with the same event
        // cannot slip through.
        builder.HasIndex(s => s.EventId)
            .IsUnique()
            .HasDatabaseName("ux_sale_event_id");

        builder.HasIndex(s => s.Number)
            .IsUnique()
            .HasDatabaseName("ux_sale_number");

        builder.HasIndex(s => new { s.LocationId, s.BusinessDate })
            .HasDatabaseName("ix_sale_location_business_date");

        builder.HasIndex(s => new { s.CashierShiftId, s.Id })
            .HasDatabaseName("ix_sale_shift");

        builder.HasMany(s => s.Items)
            .WithOne()
            .HasForeignKey(i => i.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Payment exposes no SaleId — the sale owns its payments purely through
        // the collection, so the foreign key is a shadow property.
        builder.HasMany(s => s.Payments)
            .WithOne()
            .HasForeignKey("SaleId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps one frozen line of a completed sale.</summary>
public sealed class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SaleItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sale_item", PosDbContext.SalesSchema);

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(i => i.SaleId).HasColumnName("sale_id").IsRequired();
        builder.Property(i => i.LineNumber).HasColumnName("line_no").IsRequired();
        builder.Property(i => i.ProductId).HasColumnName("product_id").IsRequired();

        builder.Property(i => i.ProductName)
            .HasColumnName("product_name")
            .HasMaxLength(SaleItem.ProductNameMaxLength)
            .IsRequired();

        builder.Property(i => i.Barcode)
            .HasColumnName("barcode")
            .HasMaxLength(SaleItem.BarcodeMaxLength);

        builder.Property(i => i.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.Property(i => i.UnitOfMeasureId).HasColumnName("uom_id").IsRequired();

        builder.Property(i => i.UnitPrice)
            .HasColumnName("unit_price")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.PriceVersion).HasColumnName("price_version").IsRequired();
        builder.Property(i => i.PriceWasOverridden).HasColumnName("price_was_overridden").IsRequired();
        builder.Property(i => i.PriceOverrideAuthorizedByUserId).HasColumnName("price_override_authorized_by_user_id");

        builder.Property(i => i.Discount)
            .HasColumnName("discount_amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.DiscountAuthorizedByUserId).HasColumnName("discount_authorized_by_user_id");

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

        // The frozen line totals re-derived by the domain at creation; a manual
        // change after completion is impossible because the aggregate exposes no
        // mutator, and the receipt triggers treat the whole row set as append-only.
        builder.Property(i => i.GrossAmount)
            .HasColumnName("gross_amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(i => i.NetAmount)
            .HasColumnName("net_amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        // The quantity accepted back across accepted returns, capped by the
        // line's own quantity. Zero until a return records against the sale.
        builder.Property(i => i.ReturnedQuantity)
            .HasColumnName("returned_quantity")
            .HasPrecision(18, Quantity.Scale)
            .IsRequired();

        builder.HasIndex(i => new { i.SaleId, i.LineNumber })
            .IsUnique()
            .HasDatabaseName("ux_sale_item_line_no");

        builder.HasIndex(i => new { i.ProductId, i.SaleId })
            .HasDatabaseName("ix_sale_item_product");
    }
}

/// <summary>Maps one payment toward a sale.</summary>
public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("payment", PosDbContext.SalesSchema);

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();

        // The sale owns its payments purely through the collection; the
        // foreign key is a shadow property because the domain type has none.
        builder.Property<SaleId>("SaleId").HasColumnName("sale_id").IsRequired();

        builder.Property(p => p.Method).HasColumnName("method").HasConversion<short>().IsRequired();

        builder.Property(p => p.Amount)
            .HasColumnName("amount")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(p => p.Tendered)
            .HasColumnName("tendered")
            .HasPrecision(19, Money.StorageScale);

        builder.Property(p => p.Change)
            .HasColumnName("change_given")
            .HasPrecision(19, Money.StorageScale);

        builder.Property(p => p.ProviderReference)
            .HasColumnName("provider_reference")
            .HasMaxLength(Payment.ProviderReferenceMaxLength);

        builder.HasIndex("SaleId", nameof(Payment.Id))
            .HasDatabaseName("ix_payment_sale");
    }
}
