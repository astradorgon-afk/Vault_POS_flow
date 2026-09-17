using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence.Conversions;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// The scoped SQLite database carried by a POS device. It contains only local
/// identity plus downloaded caches and snapshots; server identity, audit,
/// purchasing and reporting tables are deliberately absent.
/// </summary>
/// <remarks>
/// Downloaded caches, permission snapshots and the feed cursor are written only
/// by <see cref="ChangeFeedApplier"/>. Every context carries an interceptor that
/// refuses tracked writes to them and registers the SQL function their triggers
/// consult, so raw SQL is refused as well.
/// </remarks>
/// <param name="options">SQLite options for the encrypted device file.</param>
public sealed class PosDeviceDbContext(DbContextOptions<PosDeviceDbContext> options) : DbContext(options)
{
    public DbSet<DeviceStoreProfile> DeviceProfiles => Set<DeviceStoreProfile>();
    public DbSet<DeviceDocumentCounter> DocumentCounters => Set<DeviceDocumentCounter>();
    public DbSet<DeviceCachedProduct> Products => Set<DeviceCachedProduct>();
    public DbSet<DeviceCachedProductBarcode> ProductBarcodes => Set<DeviceCachedProductBarcode>();
    public DbSet<DeviceCachedProductPrice> ProductPrices => Set<DeviceCachedProductPrice>();
    public DbSet<DeviceCachedLocation> Locations => Set<DeviceCachedLocation>();
    public DbSet<DeviceCachedUser> Users => Set<DeviceCachedUser>();
    public DbSet<DevicePermissionSnapshot> PermissionSnapshots => Set<DevicePermissionSnapshot>();
    public DbSet<DeviceSyncCursor> SyncCursors => Set<DeviceSyncCursor>();

    /// <summary>Gets the applier's write window for this context instance.</summary>
    internal ChangeFeedWriteScope ChangeFeedWrites { get; } = new();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.AddInterceptors(
            ChangeFeedWriteGuardInterceptor.Instance,
            ChangeFeedWriterFunctionInterceptor.Instance);
        base.OnConfiguring(optionsBuilder);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        foreach (Type idType in StronglyTypedIds.Types)
        {
            configurationBuilder.Properties(idType).HaveConversion(StronglyTypedIds.ConverterFor(idType));
        }

        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(512);
        base.ConfigureConventions(configurationBuilder);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ModelBuilder builder = modelBuilder;

        builder.Entity<DeviceStoreProfile>(entity =>
        {
            entity.ToTable("device_profile");
            entity.HasKey(x => x.DeviceId);
            entity.Property(x => x.DeviceId).HasColumnName("device_id").ValueGeneratedNever();
            entity.Property(x => x.LocationId).HasColumnName("location_id").IsRequired();
            entity.Property(x => x.ShortCode).HasColumnName("short_code").HasMaxLength(6).IsRequired();
            entity.Property(x => x.EnrolledAtUtc).HasColumnName("enrolled_at_utc").IsRequired();
            entity.HasIndex(x => x.ShortCode).IsUnique().HasDatabaseName("ux_device_profile_short_code");
        });

        // Not applier-owned: the device is the authority for its own document
        // numbers, so this is the one table the change feed never writes and
        // application code may.
        builder.Entity<DeviceDocumentCounter>(entity =>
        {
            entity.ToTable("document_counter");
            entity.HasKey(x => new { x.DocumentType, x.PeriodKey });
            entity.Property(x => x.DocumentType).HasColumnName("document_type").HasConversion<short>();
            entity.Property(x => x.PeriodKey).HasColumnName("period_key").HasMaxLength(8);
            entity.Property(x => x.NextValue).HasColumnName("next_value").IsRequired();
        });

        builder.Entity<DeviceCachedProduct>(entity =>
        {
            OwnedByChangeFeed(entity, "cache_product");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(240).IsRequired();
            entity.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(x => x.TracksBatches).HasColumnName("tracks_batches").IsRequired();
            entity.Property(x => x.TracksExpiry).HasColumnName("tracks_expiry").IsRequired();
            entity.Property(x => x.SourceVersion).HasColumnName("source_version").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
            entity.HasIndex(x => x.Sku).IsUnique().HasDatabaseName("ux_cache_product_sku");
        });

        builder.Entity<DeviceCachedProductBarcode>(entity =>
        {
            OwnedByChangeFeed(entity, "cache_product_barcode");
            entity.HasKey(x => x.Barcode);
            entity.Property(x => x.Barcode).HasColumnName("barcode").HasMaxLength(64);
            entity.Property(x => x.ProductId).HasColumnName("product_id").IsRequired();
            entity.Property(x => x.IsPrimary).HasColumnName("is_primary").IsRequired();
            entity.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            entity.HasIndex(x => x.ProductId).HasDatabaseName("ix_cache_product_barcode_product");
        });

        builder.Entity<DeviceCachedProductPrice>(entity =>
        {
            OwnedByChangeFeed(entity, "cache_product_price");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.ProductId).HasColumnName("product_id").IsRequired();
            entity.Property(x => x.LocationId).HasColumnName("location_id");
            entity.Property(x => x.Amount).HasColumnName("amount").HasPrecision(19, 4).IsRequired();
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            entity.Property(x => x.EffectiveFromUtc).HasColumnName("effective_from_utc").IsRequired();
            entity.Property(x => x.EffectiveToUtc).HasColumnName("effective_to_utc");
            entity.HasIndex(x => new { x.ProductId, x.LocationId, x.EffectiveFromUtc })
                .HasDatabaseName("ix_cache_product_price_effective");
        });

        builder.Entity<DeviceCachedLocation>(entity =>
        {
            OwnedByChangeFeed(entity, "cache_location");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(32).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(160).IsRequired();
            entity.Property(x => x.Kind).HasColumnName("kind").HasConversion<short>().IsRequired();
            entity.Property(x => x.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(128).IsRequired();
            entity.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            entity.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            entity.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_cache_location_code");
        });

        builder.Entity<DeviceCachedUser>(entity =>
        {
            OwnedByChangeFeed(entity, "cache_user");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.UserName).HasColumnName("user_name").HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(160).IsRequired();
            entity.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(x => x.SecurityVersion).HasColumnName("security_version").IsRequired();
            entity.HasIndex(x => x.UserName).IsUnique().HasDatabaseName("ux_cache_user_name");
        });

        builder.Entity<DevicePermissionSnapshot>(entity =>
        {
            OwnedByChangeFeed(entity, "snapshot_permission");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.Permission).HasColumnName("permission").HasMaxLength(160).IsRequired();
            entity.Property(x => x.LocationId).HasColumnName("location_id");
            entity.Property(x => x.PolicyVersion).HasColumnName("policy_version").IsRequired();
            entity.Property(x => x.IssuedAtUtc).HasColumnName("issued_at_utc").IsRequired();
            entity.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
            entity.HasIndex(x => new { x.UserId, x.Permission })
                .IsUnique()
                .HasFilter("location_id IS NULL")
                .HasDatabaseName("ux_snapshot_permission_global");
            entity.HasIndex(x => new { x.UserId, x.Permission, x.LocationId })
                .IsUnique()
                .HasFilter("location_id IS NOT NULL")
                .HasDatabaseName("ux_snapshot_permission_location");
            entity.HasIndex(x => x.ExpiresAtUtc).HasDatabaseName("ix_snapshot_permission_expiry");
        });

        builder.Entity<DeviceSyncCursor>(entity =>
        {
            OwnedByChangeFeed(entity, "sync_cursor");
            entity.HasKey(x => x.Feed);
            entity.Property(x => x.Feed).HasColumnName("feed").HasMaxLength(64);
            entity.Property(x => x.Position).HasColumnName("position").IsRequired();
            entity.Property(x => x.AdvancedAtUtc).HasColumnName("advanced_at_utc").IsRequired();
        });

        ApplySqliteTypeMappings(builder);
    }

    /// <summary>
    /// Maps an applier-owned table and declares its guard triggers, which the
    /// migration creates. Declaring them also stops EF relying on
    /// <c>RETURNING</c> for these tables.
    /// </summary>
    private static void OwnedByChangeFeed<TEntity>(EntityTypeBuilder<TEntity> entity, string table)
        where TEntity : class
        => entity.ToTable(table, t =>
        {
            t.HasTrigger("trg_" + table + "_feed_only_insert");
            t.HasTrigger("trg_" + table + "_feed_only_update");
            t.HasTrigger("trg_" + table + "_feed_only_delete");
        });

    private static void ApplySqliteTypeMappings(ModelBuilder modelBuilder)
    {
        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property in entity.GetProperties())
            {
                int scale = property.GetScale() ?? Money.StorageScale;

                if (property.ClrType == typeof(decimal))
                {
                    property.SetValueConverter(new DecimalAsTextConverter(scale));
                }
                else if (property.ClrType == typeof(decimal?))
                {
                    property.SetValueConverter(new NullableDecimalAsTextConverter(scale));
                }
                else if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(new UtcDateTimeOffsetAsTextConverter());
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new NullableUtcDateTimeOffsetAsTextConverter());
                }
            }
        }
    }
}
