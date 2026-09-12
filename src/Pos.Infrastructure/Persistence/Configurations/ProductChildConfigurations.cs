using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the product price rows.</summary>
public sealed class ProductPriceConfiguration : IEntityTypeConfiguration<ProductPrice>
{
    /// <summary>The currency every persisted price is materialized in (single-currency v1).</summary>
    private const string StoredCurrencyCode = "PHP";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProductPrice> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_price", PosDbContext.CatalogSchema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(p => p.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(p => p.LocationId).HasColumnName("location_id");
        // Single-currency deployment: prices persist as amounts only (DATABASE.md
        // has no currency column on product_price), so the currency code is not
        // round-tripped; a multi-currency release adds the column and widens this.
        builder.Property(p => p.Price).HasColumnName("price")
            .HasConversion(
                money => money.Amount,
                amount => new Money(amount, StoredCurrencyCode))
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();
        builder.Property(p => p.EffectiveFromUtc).HasColumnName("effective_from_utc").IsRequired();
        builder.Property(p => p.EffectiveToUtc).HasColumnName("effective_to_utc");
        builder.Property(p => p.CreatedByUserId).HasColumnName("created_by").IsRequired();
        builder.Property(p => p.Reason).HasColumnName("reason").HasMaxLength(512);
        builder.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.Ignore(p => p.Amount);
    }
}

/// <summary>Maps the product unit conversion rows.</summary>
public sealed class ProductUnitConversionConfiguration : IEntityTypeConfiguration<ProductUnitConversion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProductUnitConversion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_unit_conversion", PosDbContext.CatalogSchema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(c => c.FromUnitId).HasColumnName("from_uom_id").IsRequired();
        builder.Property(c => c.ToUnitId).HasColumnName("to_uom_id").IsRequired();
        builder.Property(c => c.Factor).HasColumnName("factor").HasPrecision(18, 6).IsRequired();

        builder.HasIndex(c => new { c.ProductId, c.FromUnitId, c.ToUnitId })
            .IsUnique()
            .HasDatabaseName("ux_product_unit_conversion");
    }
}

/// <summary>Maps the per-location product setting rows.</summary>
public sealed class ProductLocationSettingConfiguration : IEntityTypeConfiguration<ProductLocationSetting>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProductLocationSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_location_setting", PosDbContext.CatalogSchema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(s => s.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(s => s.IsStocked).HasColumnName("is_stocked").IsRequired();
        builder.Property(s => s.MinimumStock).HasColumnName("minimum_stock").HasPrecision(18, Quantity.Scale).IsRequired();
        builder.Property(s => s.ReorderPoint).HasColumnName("reorder_point").HasPrecision(18, Quantity.Scale).IsRequired();
        builder.Property(s => s.TargetStock).HasColumnName("target_stock").HasPrecision(18, Quantity.Scale).IsRequired();
        builder.Property(s => s.MaximumStock).HasColumnName("maximum_stock").HasPrecision(18, Quantity.Scale).IsRequired();
        builder.Property(s => s.PreferredReplenishmentQuantity).HasColumnName("preferred_replenishment_quantity").HasPrecision(18, Quantity.Scale).IsRequired();

        builder.HasIndex(s => new { s.ProductId, s.LocationId })
            .IsUnique()
            .HasDatabaseName("ux_product_location");

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_product_location_thresholds",
            "minimum_stock <= reorder_point AND reorder_point <= target_stock AND target_stock <= maximum_stock"));
    }
}

/// <summary>Maps the product-supplier link rows.</summary>
public sealed class ProductSupplierConfiguration : IEntityTypeConfiguration<ProductSupplier>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProductSupplier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_supplier", PosDbContext.CatalogSchema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(s => s.SupplierId).HasColumnName("supplier_id").IsRequired();
        builder.Property(s => s.SupplierSku).HasColumnName("supplier_sku").HasMaxLength(64);
        builder.Property(s => s.LastCost).HasColumnName("last_cost").HasPrecision(19, Money.StorageScale);
        builder.Property(s => s.LeadTimeDays).HasColumnName("lead_time_days").IsRequired();
        builder.Property(s => s.MinimumOrderQuantity).HasColumnName("minimum_order_quantity").HasPrecision(18, Quantity.Scale);
        builder.Property(s => s.IsPreferred).HasColumnName("is_preferred").IsRequired();

        builder.HasIndex(s => new { s.ProductId, s.SupplierId })
            .IsUnique()
            .HasDatabaseName("ux_product_supplier");

        builder.HasIndex(s => s.IsPreferred).HasFilter("\"is_preferred\"").HasDatabaseName("ix_product_supplier_preferred");
    }
}