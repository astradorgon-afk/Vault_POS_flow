using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pos.Infrastructure.Offline;

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

/// <summary>Creates the device-only SQLite context for its independent migration set.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<PosDeviceDbContext>
{
    /// <inheritdoc />
    public PosDeviceDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<PosDeviceDbContext> builder = new();
        builder.UseSqlite(
            "Data Source=vaultflow-device-design.db;Password=vaultflow-design-only",
            sqlite =>
            {
                sqlite.MigrationsHistoryTable("__migrations_history");
                sqlite.MigrationsAssembly(typeof(SqliteDesignTimeFactory).Assembly.FullName);
            });

        return new PosDeviceDbContext(builder.Options);
    }
}
