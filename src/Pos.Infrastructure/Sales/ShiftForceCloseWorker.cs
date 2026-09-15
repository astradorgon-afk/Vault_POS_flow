using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Infrastructure.Configuration;

namespace Pos.Infrastructure.Sales;

/// <summary>
/// Periodically scans for cashier shifts that stayed open past their location's
/// <c>MaxShiftHours</c> and force-closes them, flagging each one so an abandoned
/// shift cannot silently absorb the next day's sales (POS.md §1). Runs as a
/// system-level background service with no authenticated user.
/// </summary>
/// <remarks>
/// Each shift is closed independently: a failure on one does not prevent the
/// pass from continuing to the next. The worker uses a fresh scope per pass to
/// avoid holding a long-lived DbContext.
/// </remarks>
/// <param name="services">Service provider, so each pass gets a fresh scope.</param>
/// <param name="options">Worker schedule.</param>
/// <param name="logger">Logger.</param>
public sealed class ShiftForceCloseWorker(
    IServiceScopeFactory services,
    IOptions<ShiftForceCloseOptions> options,
    ILogger<ShiftForceCloseWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ShiftForceCloseOptions settings = options.Value;

        if (settings.RunOnStartup)
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }

        using PeriodicTimer timer = new(settings.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();

        IShiftRepository repository = scope.ServiceProvider.GetRequiredService<IShiftRepository>();
        IAuditWriter audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        ISystemClock clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();

        IReadOnlyList<ShiftForceCloseCandidate> candidates = await repository
            .GetForceCloseCandidatesAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset nowUtc = clock.UtcNow;
        int closedCount = 0;
        int errorCount = 0;

        foreach (ShiftForceCloseCandidate candidate in candidates)
        {
            if (nowUtc - candidate.Shift.OpenedAtUtc <= candidate.MaxShiftHours)
            {
                continue;
            }

            Result closed = candidate.Shift.ForceClose(nowUtc);

            if (closed.IsFailure)
            {
                errorCount++;

                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(
                        "Force-close refused for shift {ShiftId}: {ErrorCode} {ErrorMessage}.",
                        candidate.Shift.Id.Value,
                        closed.Error.Code,
                        closed.Error.Message);
                }

                continue;
            }

            await audit.WriteAsync(new AuditEntry(
                AuditActions.Sales.ShiftForceClosed,
                "cashier_shift",
                candidate.Shift.Id.Value,
                Reason: FormattableString.Invariant(
                    $"Left open past the location's maximum of {candidate.MaxShiftHours.TotalHours:F0} hours; force-closed by the worker."),
                LocationId: candidate.Shift.LocationId),
                cancellationToken).ConfigureAwait(false);

            Result<CashierShiftId> saved = await repository
                .UpdateAsync(candidate.Shift, cancellationToken)
                .ConfigureAwait(false);

            if (saved.IsFailure)
            {
                errorCount++;

                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(
                        "Force-close could not be persisted for shift {ShiftId}: {ErrorCode} {ErrorMessage}.",
                        candidate.Shift.Id.Value,
                        saved.Error.Code,
                        saved.Error.Message);
                }

                continue;
            }

            closedCount++;

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Force-closed overdue shift {ShiftId} at location {LocationId}.",
                    candidate.Shift.Id.Value,
                    candidate.Shift.LocationId.Value);
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Shift force-close pass completed: {ClosedCount} shifts closed ({ErrorCount} errors).",
                closedCount,
                errorCount);
        }
    }
}