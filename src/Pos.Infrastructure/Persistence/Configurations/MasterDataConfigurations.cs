using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Catalog;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the reference master-data aggregate types.</summary>
public static class MasterDataConfigurations
{
    /// <summary>Maps the product category aggregate.</summary>
    public sealed class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<ProductCategory> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ToTable("product_category", PosDbContext.CatalogSchema);

            builder.HasKey(c => c.Id);
            builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(c => c.ParentId).HasColumnName("parent_id");
            builder.Property(c => c.Code).HasColumnName("code").HasMaxLength(ProductCategory.CodeMaxLength).IsRequired();
            builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(ProductCategory.NameMaxLength).IsRequired();
            builder.Property(c => c.SortOrder).HasColumnName("sort_order").IsRequired();
            builder.Property(c => c.IsActive).HasColumnName("is_active").IsRequired();

            builder.HasIndex(c => c.Code).IsUnique().HasDatabaseName("ux_product_category_code");

            builder.HasOne<ProductCategory>()
                .WithMany()
                .HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    /// <summary>Maps the brand aggregate.</summary>
    public sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<Brand> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ToTable("brand", PosDbContext.CatalogSchema);

            builder.HasKey(b => b.Id);
            builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(b => b.Name).HasColumnName("name").HasMaxLength(Brand.NameMaxLength).IsRequired();
            builder.Property(b => b.IsActive).HasColumnName("is_active").IsRequired();

            builder.HasIndex(b => b.Name).IsUnique().HasDatabaseName("ux_brand_name");
        }
    }

    /// <summary>Maps the unit of measure aggregate.</summary>
    public sealed class UnitOfMeasureConfiguration : IEntityTypeConfiguration<UnitOfMeasure>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<UnitOfMeasure> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ToTable("unit_of_measure", PosDbContext.CatalogSchema);

            builder.HasKey(u => u.Id);
            builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(u => u.Code).HasColumnName("code").HasMaxLength(UnitOfMeasure.CodeMaxLength).IsRequired();
            builder.Property(u => u.Name).HasColumnName("name").HasMaxLength(UnitOfMeasure.NameMaxLength).IsRequired();
            builder.Property(u => u.Kind).HasColumnName("kind").HasConversion<short>().IsRequired();
            builder.Property(u => u.DecimalPlaces).HasColumnName("decimal_places").IsRequired();

            builder.HasIndex(u => u.Code).IsUnique().HasDatabaseName("ux_uom_code");
        }
    }

    /// <summary>Maps the supplier aggregate.</summary>
    public sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<Supplier> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ToTable("supplier", PosDbContext.CatalogSchema);

            builder.HasKey(s => s.Id);
            builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(s => s.Code).HasColumnName("code").HasMaxLength(Supplier.CodeMaxLength).IsRequired();
            builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(Supplier.NameMaxLength).IsRequired();
            builder.Property(s => s.TaxId).HasColumnName("tax_id").HasMaxLength(32);
            builder.Property(s => s.PaymentTermsDays).HasColumnName("payment_terms_days").IsRequired();
            builder.Property(s => s.LeadTimeDays).HasColumnName("lead_time_days").IsRequired();
            builder.Property(s => s.IsActive).HasColumnName("is_active").IsRequired();

            builder.HasIndex(s => s.Code).IsUnique().HasDatabaseName("ux_supplier_code");
        }
    }
}