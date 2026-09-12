using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
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
