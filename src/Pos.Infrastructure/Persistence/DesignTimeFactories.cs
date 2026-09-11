using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Creates a context for <c>dotnet ef</c> at design time, for the PostgreSQL
/// migration set.
/// </summary>
/// <remarks>
/// The connection string here is a design-time placeholder: migrations are
/// generated from the model, not from a live database, and this factory is
/// never used at runtime. Real connection strings come from configuration and
/// are not committed.
/// </remarks>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    private const string DesignTimeConnection =
        "Host=localhost;Port=5432;Database=vaultflow_design;Username=design;Password=design";

    /// <inheritdoc />
    public PosDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<PosDbContext> builder = new();

        builder.UseNpgsql(
            Environment.GetEnvironmentVariable("POS_DESIGN_CONNECTION") ?? DesignTimeConnection,
            npgsql =>
            {
                npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema);
                npgsql.MigrationsAssembly(typeof(PostgresDesignTimeFactory).Assembly.FullName);
            });

        return new PosDbContext(builder.Options);
    }
}

// The SQLite migration set is added in Phase 12 alongside the device client.
// EF Core allows one design-time factory per context type, so the device
// database gets its own thin context subclass (PosDeviceDbContext) with its own
// migrations folder rather than a second factory for this one.
