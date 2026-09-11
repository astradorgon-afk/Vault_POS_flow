using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Common;
using Pos.Api.Middleware;
using Pos.Application;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Infrastructure;
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
    builder.Services.AddInfrastructure(builder.Configuration);

    // Identity arrives in Phase 2. Until then the caller is anonymous and the
    // permission evaluator denies everything, so no endpoint can be reached with
    // implicit authority.
    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
    builder.Services.AddScoped<IPermissionEvaluator, DenyAllPermissionEvaluator>();

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

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy("auth-login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            }));

        options.AddPolicy("sync-push", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Request.Headers[RequestContextMiddleware.DeviceHeader].ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
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
                    PermitLimit = 300,
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

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();

        // Development only, and only after an explicit opt-in: an API that
        // migrates itself on boot races its own replicas in production.
        if (app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
        {
            using IServiceScope scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PosDbContext>().Database.MigrateAsync();
        }
    }

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

    await app.RunAsync();
    return 0;
}
#pragma warning disable CA1031 // The top-level guard must catch everything: a
                               // start-up failure has to be logged and flushed
                               // before the process exits, or it is invisible.
catch (Exception ex)
{
    Log.Fatal(ex, "Pos.Api terminated unexpectedly during start-up.");
    return 1;
}
#pragma warning restore CA1031
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point marker so integration tests can reference the host.</summary>
public partial class Program;
