using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence.Conversions;
using Pos.Infrastructure.Persistence.Configurations;
using Pos.Infrastructure.Inventory;

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
public sealed class PosDeviceDbContext(DbContextOptions<PosDeviceDbContext> options) : DbContext(options), ILedgerStore
{
    public DbSet<DeviceStoreProfile> DeviceProfiles => Set<DeviceStoreProfile>();
    public DbSet<DeviceDocumentCounter> DocumentCounters => Set<DeviceDocumentCounter>();
    public DbSet<DeviceCachedProduct> Products => Set<DeviceCachedProduct>();
    public DbSet<DeviceCachedProductBarcode> ProductBarcodes => Set<DeviceCachedProductBarcode>();
    public DbSet<DeviceCachedProductPrice> ProductPrices => Set<DeviceCachedProductPrice>();
    public DbSet<DeviceCachedBatch> Batches => Set<DeviceCachedBatch>();
    public DbSet<DeviceCachedLocation> Locations => Set<DeviceCachedLocation>();
    public DbSet<DeviceCachedUser> Users => Set<DeviceCachedUser>();
    public DbSet<DevicePermissionSnapshot> PermissionSnapshots => Set<DevicePermissionSnapshot>();
    public DbSet<DeviceSyncCursor> SyncCursors => Set<DeviceSyncCursor>();
    public DbSet<DeviceLocalAudit> LocalAudit => Set<DeviceLocalAudit>();
    public DbSet<CashierShift> LocalShifts => Set<CashierShift>();
    public DbSet<Sale> LocalSales => Set<Sale>();
    public DbSet<SaleReceiptPrint> LocalReceiptPrints => Set<SaleReceiptPrint>();
    public DbSet<SalesReturn> LocalSalesReturns => Set<SalesReturn>();
    public DbSet<Refund> LocalRefunds => Set<Refund>();
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();
    public DbSet<DeviceSequence> Sequences => Set<DeviceSequence>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();

    /// <summary>Gets this context as its base type, for <see cref="ILedgerStore"/>.</summary>
    /// <returns>This context.</returns>
    public DbContext AsDbContext() => this;

    /// <summary>Gets the applier's write window for this context instance.</summary>
    internal ChangeFeedWriteScope ChangeFeedWrites { get; } = new();

    /// <summary>Gets the ledger's write window for this context instance.</summary>
    internal ChangeFeedWriteScope LedgerWrites { get; } = new();

    /// <inheritdoc cref="ILedgerStore.BeginLedgerWrite" />
    /// <returns>The handle that closes the window.</returns>
    public IDisposable BeginLedgerWrite() => LedgerWrites.Open();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.AddInterceptors(
            ChangeFeedWriteGuardInterceptor.Instance,
            ChangeFeedWriterFunctionInterceptor.Instance,
            LedgerWriterFunctionInterceptor.Instance);
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
            entity.Property(x => x.IsVatExempt).HasColumnName("is_vat_exempt").IsRequired();
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

        builder.Entity<DeviceCachedBatch>(entity =>
        {
            OwnedByChangeFeed(entity, "cache_batch");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.ProductId).HasColumnName("product_id").IsRequired();
            entity.Property(x => x.LotNumber).HasColumnName("lot_number").HasMaxLength(64).IsRequired();
            entity.Property(x => x.ReceivedOn).HasColumnName("received_on").IsRequired();
            entity.Property(x => x.ExpiresOn).HasColumnName("expires_on");
            entity.Property(x => x.UnitCost).HasColumnName("unit_cost").HasPrecision(19, 4).IsRequired();

            // First-expiry-first-out reads by product and expiry, and nothing else.
            entity.HasIndex(x => new { x.ProductId, x.ExpiresOn }).HasDatabaseName("ix_cache_batch_fefo");
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
            entity.Property(x => x.SettingsJson).HasColumnName("settings_json").HasMaxLength(4000);
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

        // Authoritative until synced. These are the device's own records, so
        // nothing about them is applier-owned: application code writes them and
        // the change feed never does.
        builder.Entity<DeviceLocalAudit>(entity =>
        {
            entity.ToTable("local_audit");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.Action).HasColumnName("action").HasMaxLength(160).IsRequired();
            entity.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(64).IsRequired();
            entity.Property(x => x.EntityId).HasColumnName("entity_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.DeviceId).HasColumnName("device_id");
            entity.Property(x => x.LocationId).HasColumnName("location_id");
            entity.Property(x => x.PreviousValueJson).HasColumnName("previous_value_json").HasMaxLength(4000);
            entity.Property(x => x.NewValueJson).HasColumnName("new_value_json").HasMaxLength(4000);
            entity.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();
            entity.HasIndex(x => x.RecordedAtUtc).HasDatabaseName("ix_local_audit_recorded");
        });

        // The same CashierShift aggregate the server maps, against the device's
        // own table. One aggregate, two adapters (ADR-0008): an offline shift and
        // an online one are the same business record.
        builder.Entity<CashierShift>(entity =>
        {
            entity.ToTable("local_cashier_shift");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.Number).HasColumnName("number").HasMaxLength(DocumentNumber.MaxLength).IsRequired();
            entity.Property(s => s.LocationId).HasColumnName("location_id").IsRequired();
            entity.Property(s => s.DeviceId).HasColumnName("device_id").IsRequired();
            entity.Property(s => s.CashierUserId).HasColumnName("cashier_user_id").IsRequired();
            entity.Property(s => s.OpenedAtUtc).HasColumnName("opened_at_utc").IsRequired();
            entity.Property(s => s.BusinessDate).HasColumnName("business_date").IsRequired();
            entity.Property(s => s.Status).HasColumnName("status").HasConversion<short>().IsRequired();
            entity.Property(s => s.OpeningFloat).HasColumnName("opening_float").HasPrecision(19, Money.StorageScale).IsRequired();
            entity.Property(s => s.ClosedAtUtc).HasColumnName("closed_at_utc");
            entity.Property(s => s.DeclaredCash).HasColumnName("declared_cash").HasPrecision(19, Money.StorageScale);
            entity.Property(s => s.CountedCash).HasColumnName("counted_cash").HasPrecision(19, Money.StorageScale);
            entity.Property(s => s.CashVariance).HasColumnName("cash_variance").HasPrecision(19, Money.StorageScale);
            entity.Property(s => s.IsForceClosed).HasColumnName("is_force_closed").IsRequired();
            entity.HasIndex(s => s.Number).IsUnique().HasDatabaseName("ux_local_cashier_shift_number");
            entity.HasIndex(s => s.Status).HasDatabaseName("ix_local_cashier_shift_status");
        });

        builder.Entity<DeviceSequence>(entity =>
        {
            entity.ToTable("device_sequence");
            entity.HasKey(x => x.Name);
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(32);
            entity.Property(x => x.NextValue).HasColumnName("next_value").IsRequired();
        });

        builder.Entity<OutboxEvent>(entity =>
        {
            entity.ToTable("local_outbox_event");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).HasColumnName("event_id").ValueGeneratedNever();
            entity.Property(e => e.DeviceSequence).HasColumnName("device_sequence").IsRequired();
            entity.Property(e => e.Type).HasColumnName("type").HasConversion<short>().IsRequired();
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json").HasMaxLength(64000).IsRequired();
            entity.Property(e => e.PayloadHash).HasColumnName("payload_hash").IsRequired();
            entity.Property(e => e.DeviceId).HasColumnName("device_id").IsRequired();
            entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(e => e.LocationId).HasColumnName("location_id").IsRequired();
            entity.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
            entity.Property(e => e.DeviceUptimeTicks).HasColumnName("device_uptime_ticks").IsRequired();
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id").IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<short>().IsRequired();
            entity.Property(e => e.AttemptCount).HasColumnName("attempt_count").IsRequired();
            entity.Property(e => e.LastAttemptAtUtc).HasColumnName("last_attempt_at_utc");
            entity.Property(e => e.NextRetryAtUtc).HasColumnName("next_retry_at_utc");
            entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(2000);
            entity.Property(e => e.ServerResponseJson).HasColumnName("server_response_json").HasMaxLength(8000);

            // Uploads go in device order and a batch stops at the first deferral,
            // so the queue is always read by sequence within status.
            entity.HasIndex(e => e.DeviceSequence).IsUnique().HasDatabaseName("ux_local_outbox_sequence");
            entity.HasIndex(e => new { e.Status, e.DeviceSequence }).HasDatabaseName("ix_local_outbox_status_sequence");
        });

        // The sale aggregate, mapped from the server's own configurations so an
        // offline sale and an online one are the same record in the same shape.
        builder.ApplyConfiguration(new SaleConfiguration());
        builder.ApplyConfiguration(new SaleItemConfiguration());
        builder.ApplyConfiguration(new PaymentConfiguration());
        builder.Entity<Sale>().ToTable("local_sale");
        builder.Entity<SaleItem>().ToTable("local_sale_item");
        builder.Entity<Payment>().ToTable("local_payment");
        builder.ApplyConfiguration(new SaleReceiptPrintConfiguration());
        builder.Entity<SaleReceiptPrint>().ToTable("local_sale_receipt_print");
        builder.ApplyConfiguration(new SalesReturnConfiguration());
        builder.ApplyConfiguration(new SalesReturnItemConfiguration());
        builder.ApplyConfiguration(new RefundConfiguration());
        builder.Entity<SalesReturn>().ToTable("local_sales_return");
        builder.Entity<SalesReturnItem>().ToTable("local_sales_return_item");
        builder.Entity<Refund>().ToTable("local_refund");

        // The server's own ledger mappings, applied verbatim so the two
        // databases cannot drift in column shape, index or concurrency token,
        // then renamed to the device's local_* convention. A change to the
        // server's mapping reaches the device with it.
        builder.ApplyConfiguration(new InventoryMovementConfiguration());
        builder.ApplyConfiguration(new InventoryBalanceConfiguration());
        builder.Entity<InventoryMovement>(entity => entity.ToTable(
            "local_inventory_movement",
            t =>
            {
                t.HasTrigger("trg_local_inventory_movement_immutable_update");
                t.HasTrigger("trg_local_inventory_movement_immutable_delete");
            }));
        builder.Entity<InventoryBalance>(entity => entity.ToTable(
            "local_inventory_balance",
            t =>
            {
                t.HasTrigger("trg_local_inventory_balance_ledger_only_insert");
                t.HasTrigger("trg_local_inventory_balance_ledger_only_update");
                t.HasTrigger("trg_local_inventory_balance_ledger_only_delete");
            }));

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
