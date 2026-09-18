using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Executes due, replayable synchronization failures. Events without a
/// registered handler remain in the operator queue and are never hot-looped.
/// </summary>
public sealed class SyncRetryWorker(
    IServiceScopeFactory scopes,
    ILogger<SyncRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ProcessDueAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        SyncFailureService failures = scope.ServiceProvider.GetRequiredService<SyncFailureService>();
        ISystemClock clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        IReadOnlyList<Guid> ids = await failures
            .GetDueFailureIdsAsync(clock.UtcNow, 20, cancellationToken)
            .ConfigureAwait(false);

        SyncPushService replay = scope.ServiceProvider.GetRequiredService<SyncPushService>();
        foreach (Guid id in ids)
        {
            try
            {
                Result result = await replay.RetryFailureAsync(id, cancellationToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "Synchronization retry {FailureId} did not complete: {Code} {Message}.",
                        id,
                        result.Error.Code,
                        result.Error.Message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Synchronization retry {FailureId} failed unexpectedly.", id);
            }
        }
    }
}
