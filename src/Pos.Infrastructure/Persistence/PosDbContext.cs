using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Organizations;
using Pos.Domain.Purchasing;
using Pos.Domain.Transfers;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence.Conversions;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// The application database context. The same context and the same mappings run
/// against PostgreSQL on the server and SQLite on a device, so an offline sale
/// writes the same rows an online sale does.
/// </summary>
/// <param name="options">Context options, including the provider.</param>
public class PosDbContext(DbContextOptions<PosDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    /// <summary>Schema holding inventory tables.</summary>
    public const string InventorySchema = "inventory";

    /// <summary>Schema holding core reference and identity tables.</summary>
    public const string CoreSchema = "core";

    /// <summary>Schema holding the audit log.</summary>
    public const string AuditSchema = "audit";

    /// <summary>Schema holding synchronization tables.</summary>
    public const string SyncSchema = "sync";

    /// <summary>Schema holding the product catalogue and reference master data.</summary>
    public const string CatalogSchema = "catalog";

    /// <summary>Schema holding purchasing documents: orders, lines and decisions.</summary>
    public const string PurchasingSchema = "purchasing";

    /// <summary>Schema holding transfer orders, allocations and custody events.</summary>
    public const string TransfersSchema = "transfers";

    /// <summary>
    /// Gets the append-only inventory ledger. Insert only: the interceptor and
    /// the database triggers both reject updates and deletes.
    /// </summary>
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();

    /// <summary>
    /// Gets the balance projection. Written only by the ledger service, and
    /// independently guarded by a database trigger that compares each change
    /// against the movements inserted in the same transaction.
    /// </summary>
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();

    /// <summary>
    /// Gets the append-only audit log. Subject to the same four guards as the
    /// ledger; an audit trail that can be edited is not a trail.
    /// </summary>
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    /// <summary>Gets the permission catalogue, seeded from code.</summary>
    public DbSet<PermissionRecord> Permissions => Set<PermissionRecord>();

    /// <summary>Gets the role-to-permission grants.</summary>
    public DbSet<RolePermissionGrant> RolePermissions => Set<RolePermissionGrant>();

    /// <summary>Gets the per-user permission overrides.</summary>
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();

    /// <summary>Gets the locations each user may act in.</summary>
    public DbSet<UserLocationAssignment> UserLocations => Set<UserLocationAssignment>();

    /// <summary>Gets the authorization policy version row.</summary>
    public DbSet<AuthorizationPolicyVersion> PolicyVersion => Set<AuthorizationPolicyVersion>();

    /// <summary>Gets the registered devices.</summary>
    public DbSet<Device> Devices => Set<Device>();

    /// <summary>Gets the outstanding and historical device enrolment codes.</summary>
    public DbSet<DeviceEnrolmentCode> DeviceEnrolmentCodes => Set<DeviceEnrolmentCode>();

    /// <summary>Gets the device sign-in sessions.</summary>
    public DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();

    /// <summary>Gets the product catalogue.</summary>
    public DbSet<Product> Products => Set<Product>();

    /// <summary>Gets product barcodes.</summary>
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();

    /// <summary>Gets product price rows.</summary>
    public DbSet<ProductPrice> ProductPrices => Set<ProductPrice>();

    /// <summary>Gets the organizations.</summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    /// <summary>Gets the locations.</summary>
    public DbSet<Location> Locations => Set<Location>();

    /// <summary>Gets the product categories.</summary>
    public DbSet<ProductCategory> Categories => Set<ProductCategory>();

    /// <summary>Gets the brands.</summary>
    public DbSet<Brand> Brands => Set<Brand>();

    /// <summary>Gets the units of measure.</summary>
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();

    /// <summary>Gets the suppliers.</summary>
    public DbSet<Supplier> Suppliers => Set<Supplier>();

    /// <summary>Gets the purchase orders.</summary>
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    /// <summary>Gets the purchase order lines.</summary>
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();

    /// <summary>Gets the purchase order approval decisions.</summary>
    public DbSet<PurchaseApproval> PurchaseApprovals => Set<PurchaseApproval>();

    /// <summary>Gets the goods receipts.</summary>
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();

    /// <summary>Gets the goods receipt lines.</summary>
    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();

    /// <summary>Gets the receiving discrepancies.</summary>
    public DbSet<ReceivingDiscrepancy> ReceivingDiscrepancies => Set<ReceivingDiscrepancy>();

    /// <summary>Gets the direct-to-store delivery authorizations.</summary>
    public DbSet<DirectDeliveryAuthorization> DirectDeliveryAuthorizations => Set<DirectDeliveryAuthorization>();

    /// <summary>Gets the supplier returns.</summary>
    public DbSet<SupplierReturn> SupplierReturns => Set<SupplierReturn>();

    /// <summary>Gets the supplier return lines.</summary>
    public DbSet<SupplierReturnLine> SupplierReturnLines => Set<SupplierReturnLine>();

    /// <summary>Gets the transfer orders.</summary>
    public DbSet<Transfer> Transfers => Set<Transfer>();

    /// <summary>Gets the requested lines of transfer orders.</summary>
    public DbSet<TransferLine> TransferLines => Set<TransferLine>();

    /// <summary>Gets the picking allocations of transfer orders.</summary>
    public DbSet<TransferPickAllocation> TransferAllocations => Set<TransferPickAllocation>();

    /// <summary>Gets the arrival discrepancies recorded against transfers.</summary>
    public DbSet<TransferDiscrepancy> TransferDiscrepancies => Set<TransferDiscrepancy>();

    /// <summary>Gets the custody timeline of transfer orders.</summary>
    public DbSet<TransferCustodyEvent> TransferCustodyEvents => Set<TransferCustodyEvent>();

    /// <summary>Gets the received lots of batch-tracked products.</summary>
    public DbSet<Batch> Batches => Set<Batch>();

    /// <summary>
    /// Gets the central document counter rows. Advanced exclusively by raw upsert
    /// SQL; the change tracker never writes these rows.
    /// </summary>
    public DbSet<DocumentCounter> DocumentCounters => Set<DocumentCounter>();

    /// <summary>Gets the per-location product settings.</summary>
    public DbSet<ProductLocationSetting> ProductLocationSettings => Set<ProductLocationSetting>();

    /// <summary>Gets the product unit conversions.</summary>
    public DbSet<ProductUnitConversion> ProductUnitConversions => Set<ProductUnitConversion>();

    /// <summary>Gets the product-supplier links.</summary>
    public DbSet<ProductSupplier> ProductSuppliers => Set<ProductSupplier>();

    /// <summary>Gets the refresh tokens, stored only as hashes.</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>Gets the sign-in attempt history used for throttling and review.</summary>
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

    /// <summary>Gets a value indicating whether this context is running on SQLite.</summary>
    public bool IsSqlite => Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true;

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Strongly typed identifiers map to plain UUID columns. Registering them
        // by convention means a new identifier type cannot be forgotten.
        foreach (Type idType in StronglyTypedIds.Types)
        {
            configurationBuilder.Properties(idType).HaveConversion(StronglyTypedIds.ConverterFor(idType));
        }

        // Money and quantity precision is declared once, here, so no individual
        // mapping can quietly widen or narrow it.
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);

        configurationBuilder.Properties<string>().HaveMaxLength(512);

        base.ConfigureConventions(configurationBuilder);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.HasDefaultSchema(CoreSchema);

        // Identity's own tables first, then our configurations, which rename
        // them to the project's snake_case convention.
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(PosDbContext).Assembly);

        if (IsSqlite)
        {
            ApplySqliteTypeMappings(builder);
        }
    }

    /// <summary>
    /// Replaces provider-native types that SQLite cannot represent safely.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <remarks>
    /// Decimals become fixed-scale text rather than REAL, and instants become
    /// round-trippable UTC strings. Both substitutions exist to stop silent
    /// precision loss on device databases.
    /// </remarks>
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
