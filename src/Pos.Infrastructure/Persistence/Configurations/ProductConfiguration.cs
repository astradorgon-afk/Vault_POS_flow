using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the product aggregate and its owned tables.</summary>
public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var skuConverter = new ValueConverter<Sku, string>(
            sku => sku.Value,
            value => string.IsNullOrEmpty(value) ? Sku.FromTrustedSource(string.Empty) : Sku.FromTrustedSource(value));

        builder.ToTable("product", PosDbContext.CatalogSchema);

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(p => p.Sku).HasColumnName("sku").HasConversion(skuConverter).HasMaxLength(Sku.MaxLength).IsRequired();
        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(p => p.Description).HasColumnName("description").HasMaxLength(1024);
        builder.Property(p => p.CategoryId).HasColumnName("category_id").IsRequired();
        builder.Property(p => p.BrandId).HasColumnName("brand_id");
        builder.Property(p => p.PrimarySupplierId).HasColumnName("primary_supplier_id");
        builder.Property(p => p.BaseUnitOfMeasureId).HasColumnName("base_uom_id").IsRequired();
        builder.Property(p => p.TaxCode).HasColumnName("tax_code").HasMaxLength(16);
        builder.Property(p => p.IsVatExempt).HasColumnName("is_vat_exempt").IsRequired();
        builder.Property(p => p.DefaultPurchaseCost).HasColumnName("default_purchase_cost").HasPrecision(19, Money.StorageScale).IsRequired();
        builder.Property(p => p.TracksBatches).HasColumnName("tracks_batches").IsRequired();
        builder.Property(p => p.TracksExpiry).HasColumnName("tracks_expiry").IsRequired();
        builder.Property(p => p.ShelfLifeDays).HasColumnName("shelf_life_days");
        builder.Property(p => p.ImageRef).HasColumnName("image_ref").HasMaxLength(256);
        builder.Property(p => p.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(p => p.DiscontinuedOn).HasColumnName("discontinued_on");
        builder.Property(p => p.CreatedByUserId).HasColumnName("created_by").IsRequired();
        builder.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(p => p.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        builder.HasIndex(p => p.Sku).IsUnique().HasDatabaseName("ux_product_sku");
        builder.HasIndex(p => p.CategoryId).HasFilter("\"is_active\"").HasDatabaseName("ix_product_category");
        builder.HasIndex(p => p.Name).HasDatabaseName("ix_product_name");

        builder.HasMany(p => p.Barcodes).WithOne()
            .HasForeignKey(b => b.ProductId).HasPrincipalKey(p => p.Id)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Prices).WithOne()
            .HasForeignKey(price => price.ProductId).HasPrincipalKey(p => p.Id)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.UnitConversions).WithOne()
            .HasForeignKey(c => c.ProductId).HasPrincipalKey(p => p.Id)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.LocationSettings).WithOne()
            .HasForeignKey(s => s.ProductId).HasPrincipalKey(p => p.Id)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Suppliers).WithOne()
            .HasForeignKey(s => s.ProductId).HasPrincipalKey(p => p.Id)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps the product barcode rows.</summary>
public sealed class ProductBarcodeConfiguration : IEntityTypeConfiguration<ProductBarcode>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProductBarcode> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_barcode", PosDbContext.CatalogSchema);
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(b => b.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(b => b.Value).HasColumnName("barcode").HasMaxLength(Barcode.MaxLength).IsRequired();
        builder.Property(b => b.Symbology).HasColumnName("symbology").HasConversion<short>().IsRequired();
        builder.Property(b => b.UnitOfMeasureId).HasColumnName("uom_id").IsRequired();
        builder.Property(b => b.PackQuantity).HasColumnName("pack_quantity").HasPrecision(18, Quantity.Scale).IsRequired();
        builder.Property(b => b.IsPrimary).HasColumnName("is_primary").IsRequired();
        builder.Property(b => b.CreatedByUserId).HasColumnName("created_by").IsRequired();
        builder.Property(b => b.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.Ignore(b => b.Barcode);

        builder.HasIndex(b => b.Value).IsUnique().HasDatabaseName("ux_product_barcode_value");

        builder.HasIndex(b => new { b.ProductId, b.IsPrimary })
            .HasFilter("\"is_primary\"")
            .IsUnique()
            .HasDatabaseName("ux_product_barcode_one_primary");
    }
}