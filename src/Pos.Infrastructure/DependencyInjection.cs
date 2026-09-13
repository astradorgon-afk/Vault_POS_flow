using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Inventory;
using Pos.Application.Purchasing;
using Pos.Application.Quarantine;
using Pos.Application.Receipts;
using Pos.Application.Transfers;
using Pos.Infrastructure.Auditing;
using Pos.Infrastructure.Common;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Devices;
using Pos.Infrastructure.Identity;
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
    /// <summary>Adds persistence, identity and the inventory ledger.</summary>
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

        services.AddInfrastructureOptions(configuration);
        services.AddPersistence(configuration, provider);
        services.AddIdentityServices(configuration);

        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped<IMasterDataRepository, MasterDataRepository>();
        services.TryAddSingleton<ISystemClock, SystemClock>();
        services.TryAddScoped<IAuditWriter, AuditWriter>();
        // Real policies now come from each location's own settings. The strict
        // provider remains for tests that need the conservative baseline.
        services.TryAddScoped<ILedgerPolicyProvider, LocationSettingsLedgerPolicyProvider>();
        services.TryAddScoped<INegativeStockAttemptRecorder, NegativeStockAttemptRecorder>();
        services.TryAddScoped<IInventoryLedger, InventoryLedger>();
        services.TryAddScoped<IBalanceReconciler, BalanceReconciler>();
        services.TryAddScoped<IDocumentNumberGenerator, DocumentNumberGenerator>();
        services.TryAddScoped<IPurchaseOrderRepository, PurchaseOrderRepository>();
        services.TryAddScoped<ITransferRepository, TransferRepository>();
        services.TryAddScoped<IQuarantineRepository, QuarantineRepository>();
        services.TryAddScoped<IReceiptRepository, ReceiptRepository>();

        // The reconciliation tripwire is optional so a host can run without it
        // (tests, short-lived tools); when enabled it only reads.
        ReconciliationOptions reconciliation = configuration
            .GetSection(ReconciliationOptions.SectionName)
            .Get<ReconciliationOptions>() ?? new ReconciliationOptions();

        if (reconciliation.Enabled)
        {
            services.AddHostedService<BalanceReconcilerWorker>();
        }

        return services;
    }

    /// <summary>
    /// Binds and validates every configuration section at start-up.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// <c>ValidateOnStart</c> turns a missing signing key or a malformed
    /// currency code into a refusal to boot rather than a failure on the first
    /// request that happens to need it — which, for a token signing key, would
    /// be the first sign-in of the working day.
    /// </remarks>
    public static IServiceCollection AddInfrastructureOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.SigningKeyPem),
                "Jwt:SigningKeyPem must be supplied through the environment or user secrets.")
            .ValidateOnStart();

        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<OrganizationOptions>()
            .Bind(configuration.GetSection(OrganizationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                o => o.ApprovalLimits.Tier1 <= o.ApprovalLimits.Tier2
                     && o.ApprovalLimits.Tier2 <= o.ApprovalLimits.Tier3,
                "Approval tier ceilings must increase: Tier1 <= Tier2 <= Tier3.")
            .ValidateOnStart();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<SeedingOptions>()
            .Bind(configuration.GetSection(SeedingOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ReconciliationOptions>()
            .Bind(configuration.GetSection(ReconciliationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<MaintenanceOptions>()
            .Bind(configuration.GetSection(MaintenanceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<EmergencyTransfersOptions>()
            .Bind(configuration.GetSection(EmergencyTransfersOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Application-layer command handlers are activated by the dispatcher
        // outside the Options pattern, so the bound instance is registered
        // directly as the configuration snapshot the options pipeline produced.
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmergencyTransfersOptions>>().Value);

        // Optional by design: most installations have users already, and the
        // bootstrap path must stay shut unless someone deliberately opens it.
        services.AddOptions<BootstrapOwnerOptions>()
            .Bind(configuration.GetSection(BootstrapOwnerOptions.SectionName))
            .ValidateDataAnnotations();

        return services;
    }

    /// <summary>Adds the database context and its interceptors.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="provider">Which database provider to use.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        PersistenceProvider provider)
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
                // No EnableRetryOnFailure: a retrying execution strategy refuses
                // user-initiated transactions, and the unit-of-work behaviour, the
                // ledger, the reconciler and the development seeder all open one.
                // Contention is retried where it is safe to replay (the ledger's
                // projection step), not by re-running a whole command blindly.
                options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema));
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

        return services;
    }

    /// <summary>Adds ASP.NET Core Identity and the authorization services built on it.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddIdentityServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        SecurityOptions security = configuration
            .GetSection(SecurityOptions.SectionName)
            .Get<SecurityOptions>() ?? new SecurityOptions();

        services.AddIdentityCore<AppUser>(options =>
        {
            // NIST SP 800-63B: length beats composition rules, which mostly
            // teach people to append an exclamation mark.
            options.Password.RequiredLength = security.MinimumPasswordLength;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredUniqueChars = 4;

            options.Lockout.DefaultLockoutTimeSpan = security.LockoutDuration;
            options.Lockout.MaxFailedAccessAttempts = security.MaxFailedAccessAttempts;
            options.Lockout.AllowedForNewUsers = true;

            options.User.RequireUniqueEmail = false;
            options.SignIn.RequireConfirmedAccount = false;
        })
        .AddRoles<AppRole>()
        .AddEntityFrameworkStores<PosDbContext>()
        // Only the authenticator provider is registered. Email and SMS token
        // providers would be dead weight: this system has no mail transport, and
        // a code sent by SMS is the weakest second factor on offer.
        .AddTokenProvider<AuthenticatorTokenProvider<AppUser>>(TokenOptions.DefaultAuthenticatorProvider);

        // Identity's default iteration count trails what current hardware makes
        // sensible, so it is configured explicitly and can be raised over time;
        // each hash records the parameters it was made with, so raising it does
        // not invalidate existing passwords.
        services.Configure<PasswordHasherOptions>(options =>
        {
            options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            options.IterationCount = security.PasswordHashIterations;
        });

        services.AddMemoryCache();

        services.TryAddSingleton<SigningKeyRing>();
        services.TryAddScoped<ITokenService, JwtTokenService>();
        services.TryAddScoped<IPolicyVersionProvider, PolicyVersionProvider>();
        services.TryAddScoped<DatabasePermissionEvaluator>();
        services.TryAddScoped<IPermissionEvaluator>(sp => sp.GetRequiredService<DatabasePermissionEvaluator>());
        services.TryAddScoped<ApprovalGate>();
        services.TryAddScoped<IApprovalGate>(sp => sp.GetRequiredService<ApprovalGate>());
        services.TryAddScoped<IAuthenticationService, AuthenticationService>();
        services.TryAddScoped<IDeviceService, DeviceService>();
        services.TryAddScoped<AdministrationSafeguards>();
        services.TryAddScoped<IUserAdministration, UserAdministrationService>();
        services.TryAddScoped<IRoleAdministration, RoleAdministrationService>();
        services.TryAddScoped<ITwoFactorEnrolment, TwoFactorEnrolmentService>();
        services.TryAddScoped<IdentitySeeder>();
        services.TryAddScoped<BootstrapOwnerSeeder>();
        services.TryAddScoped<DevelopmentDataSeeder>();

        return services;
    }
}
