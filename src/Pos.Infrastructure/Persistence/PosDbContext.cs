using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Persistence.Conversions;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// The application database context. The same context and the same mappings run
/// against PostgreSQL on the server and SQLite on a device, so an offline sale
/// writes the same rows an online sale does.
/// </summary>
/// <param name="options">Context options, including the provider.</param>
public class PosDbContext(DbContextOptions<PosDbContext> options) : DbContext(options)
{
    /// <summary>Schema holding inventory tables.</summary>
    public const string InventorySchema = "inventory";

    /// <summary>Schema holding core reference and identity tables.</summary>
    public const string CoreSchema = "core";

    /// <summary>Schema holding the audit log.</summary>
    public const string AuditSchema = "audit";

    /// <summary>Schema holding synchronization tables.</summary>
    public const string SyncSchema = "sync";

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
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(CoreSchema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PosDbContext).Assembly);

        if (IsSqlite)
        {
            ApplySqliteTypeMappings(modelBuilder);
        }

        base.OnModelCreating(modelBuilder);
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
