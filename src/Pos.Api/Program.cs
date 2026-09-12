using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Api.Endpoints;
using Pos.Api.Middleware;
using Pos.Application;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Infrastructure;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;

// Bootstrap logger: captures failures that happen before configuration is read,
// which is exactly when a missing secret or a bad connection string shows up.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProperty("Application", "Pos.Api"));

    builder.Services.AddProblemDetails();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddOpenApi();

    builder.Services.AddApplication();

    PersistenceProvider persistence = string.Equals(
        builder.Configuration[$"{DatabaseOptions.SectionName}:Provider"],
        nameof(PersistenceProvider.Sqlite),
        StringComparison.OrdinalIgnoreCase)
        ? PersistenceProvider.Sqlite
        : PersistenceProvider.Postgres;

    builder.Services.AddInfrastructure(builder.Configuration, persistence);

    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer();

    // Validation parameters are supplied by ConfigureJwtBearerOptions, which can
    // take the signing key ring from the container.
    builder.Services
        .AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();

    builder.Services.AddScoped<PosJwtBearerEvents>();

    builder.Services.AddAuthorization();
    builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
    builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

    builder.Services.AddHealthChecks()
        .AddDbContextCheck<PosDbContext>("database");

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Populated from configuration in production so only the known reverse
        // proxy may rewrite the caller's address.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    RateLimitOptions rateLimits = builder.Configuration
        .GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy("auth-login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.LoginPermitLimit,
                Window = TimeSpan.FromMinutes(rateLimits.LoginWindowMinutes),
                QueueLimit = 0,
            }));

        options.AddPolicy("auth-refresh", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Request.Headers[RequestContextMiddleware.DeviceHeader].ToString() is { Length: > 0 } device
                ? device
                : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.RefreshPermitLimit,
                Window = TimeSpan.FromMinutes(rateLimits.RefreshWindowMinutes),
                QueueLimit = 0,
            }));

        options.AddPolicy("sync-push", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Request.Headers[RequestContextMiddleware.DeviceHeader].ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.SyncPushPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.User.Identity?.Name
                              ?? httpContext.Connection.RemoteIpAddress?.ToString()
                              ?? "anonymous",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimits.GlobalPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
    });

    WebApplication app = builder.Build();

    app.UseForwardedHeaders();
    app.UseMiddleware<RequestContextMiddleware>();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseSerilogRequestLogging(options =>
        options.GetLevel = (httpContext, _, exception) =>
            exception is not null || httpContext.Response.StatusCode >= 500
                ? LogEventLevel.Error
                : LogEventLevel.Information);

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseRateLimiter();

    // Unhandled exceptions never reach the client as detail: the log carries the
    // exception, the response carries only a code and the correlation id.
    app.UseExceptionHandler(handler => handler.Run(async context =>
    {
        IExceptionHandlerFeature? feature = context.Features.Get<IExceptionHandlerFeature>();

        Log.Error(feature?.Error, "Unhandled exception while handling {Path}.", context.Request.Path);

        Guid correlationId =
            context.Items.TryGetValue(RequestContextMiddleware.CorrelationItemKey, out object? raw) && raw is Guid id
                ? id
                : Guid.Empty;

        IResult problem = ProblemDetailsMapping.ToProblem(
            Result.Failure(new Error(
                "server.unexpected",
                "An unexpected error occurred.",
                ErrorType.Unexpected)),
            correlationId);

        await problem.ExecuteAsync(context).ConfigureAwait(false);
    }));

    app.UseAuthentication();
    app.UseAuthorization();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    await app.PrepareDatabaseAsync().ConfigureAwait(false);

    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false,
    }).AllowAnonymous();

    app.MapHealthChecks("/health/ready").AllowAnonymous();

    app.MapGet("/api/v1/meta", (ICurrentUser user) => TypedResults.Ok(new
    {
        application = "VaultFlow",
        apiVersion = "v1",
        serverTimeUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        correlationId = user.CorrelationId.Value,
    }))
    .WithName("GetApiMetadata")
    .AllowAnonymous();

    app.MapAuthEndpoints();
    app.MapDeviceEndpoints();
    app.MapLocationEndpoints();
    app.MapCatalogEndpoints();
    app.MapInventoryEndpoints();
    app.MapPurchaseEndpoints();

    await app.RunAsync();
    return 0;
}
#pragma warning disable CA1031 // The top-level guard must catch everything: a
                               // start-up failure has to be logged and flushed
                               // before the process exits, or it is invisible.
//
// The filter matters. WebApplicationFactory starts the host by running this same
// entry point and intercepting Run() with an internal exception of its own.
// Swallowing that would leave every integration test reporting "the entry point
// exited without ever building an IHost" instead of running.
catch (Exception ex) when (ex is not HostAbortedException
                           && !string.Equals(ex.GetType().Name, "StopTheHostException", StringComparison.Ordinal))
{
    Log.Fatal(ex, "Pos.Api terminated unexpectedly during start-up.");
    return 1;
}
#pragma warning restore CA1031
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Start-up database work.</summary>
internal static class DatabaseStartup
{
    /// <summary>
    /// Applies migrations where configured, and always brings the authorization
    /// tables into line with the code catalogue.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>A task that completes when preparation finishes.</returns>
    /// <remarks>
    /// Migrations run here only in development and only on an explicit opt-in:
    /// replicas racing to migrate on boot is a well-known way to take a system
    /// down, and production uses a one-shot migration job instead.
    /// <para>
    /// Seeding is different. It is idempotent, it never drops anything, and a
    /// deployment whose permission rows lag the code would fail authorization
    /// checks that look correct in source, so it runs every time.
    /// </para>
    /// </remarks>
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        using IServiceScope scope = app.Services.CreateScope();

        DatabaseOptions database = scope.ServiceProvider
            .GetRequiredService<IOptions<DatabaseOptions>>().Value;

        PosDbContext context = scope.ServiceProvider.GetRequiredService<PosDbContext>();

        if (database.ApplyMigrationsOnStartup && app.Environment.IsDevelopment())
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        if (!await context.Database.CanConnectAsync().ConfigureAwait(false))
        {
            Log.Warning("The database is not reachable at start-up; skipping the authorization seed.");
            return;
        }

        IdentitySeeder seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
        SeedSummary summary = await seeder.SeedAsync(CancellationToken.None).ConfigureAwait(false);

        Log.Information(
            "Authorization seed complete: {Permissions} permissions, {Roles} roles, {Grants} grants added.",
            summary.PermissionsAdded,
            summary.RolesAdded,
            summary.GrantsAdded);

        DevelopmentDataSeeder development = scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>();
        DevelopmentDataSummary developmentSummary =
            await development.SeedAsync(CancellationToken.None).ConfigureAwait(false);

        if (developmentSummary != DevelopmentDataSummary.None)
        {
            Log.Information(
                "Development seed complete: {Locations} locations, {Products} products, {Accounts} accounts.",
                developmentSummary.LocationsCreated,
                developmentSummary.ProductsCreated,
                developmentSummary.AccountsCreated);
        }

        BootstrapOwnerSeeder bootstrap = scope.ServiceProvider.GetRequiredService<BootstrapOwnerSeeder>();
        await bootstrap.SeedAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>Entry point marker so integration tests can reference the host.</summary>
public partial class Program;
