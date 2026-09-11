using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Application.Common.Abstractions;
using Pos.Infrastructure.Common;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;

namespace Pos.Infrastructure;

/// <summary>Which database a host is talking to.</summary>
public enum PersistenceProvider
{
    /// <summary>PostgreSQL: the authoritative server database.</summary>
    Postgres = 0,

    /// <summary>SQLite: a device's local database.</summary>
    Sqlite = 1,
}

/// <summary>Registers infrastructure services with the dependency injection container.</summary>
public static class DependencyInjection
{
    /// <summary>Adds persistence and the inventory ledger.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="provider">Which database provider to use.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The connection string is missing.</exception>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        PersistenceProvider provider = PersistenceProvider.Postgres)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionName = provider == PersistenceProvider.Postgres ? "Postgres" : "Sqlite";
        string? connectionString = configuration.GetConnectionString(connectionName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Connection string {connectionName} is not configured. Supply it through environment variables or user secrets; it is never committed."));
        }

        services.AddSingleton<AppendOnlyInterceptor>();

        services.AddDbContext<PosDbContext>((sp, options) =>
        {
            if (provider == PersistenceProvider.Postgres)
            {
                options.UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema);
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorCodesToAdd: null);
                });
            }
            else
            {
                options.UseSqlite(connectionString, sqlite =>
                    sqlite.MigrationsHistoryTable("__migrations_history"));
            }

            options.AddInterceptors(sp.GetRequiredService<AppendOnlyInterceptor>());

            // Tracked entities are the exception, not the rule: reads are
            // projections and should never accidentally write back.
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddSingleton<ISystemClock, SystemClock>();
        services.TryAddScoped<ILedgerPolicyProvider, StrictLedgerPolicyProvider>();
        services.TryAddScoped<IInventoryLedger, InventoryLedger>();

        return services;
    }
}
