using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Inventory;

/// <summary>
/// Handles <see cref="RunExpiryCommand"/>: validates the location, delegates
/// the scan and posting to <see cref="IExpiryService"/>, and writes the audit
/// entry.
/// </summary>
/// <remarks>
/// The authorization behaviour enforces the <c>inventory.expiry.run</c>
/// permission before this handler runs. The handler only needs the current user
/// to populate the <see cref="LedgerActor"/> stamped onto each posted movement.
/// </remarks>
public sealed class RunExpiryCommandHandler(
    IExpiryService expiryService,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<RunExpiryCommand, ExpiryRunResult>
{
    /// <inheritdoc />
    public async Task<Result<ExpiryRunResult>> HandleAsync(
        RunExpiryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        LedgerActor actor = new(
            CreatedBy: currentUser.UserId ?? UserId.Empty,
            ApprovedBy: null,
            Device: currentUser.DeviceId,
            Correlation: currentUser.CorrelationId);

        Result<ExpiryRunResult> result = await expiryService
            .PostExpiryRunAsync(command.LocationId, actor, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            ExpiryRunResult run = result.Value;

            await AuditAsync(
                    audit,
                    AuditActions.Expiry.ExpiryRunPosted,
                    command.LocationId,
                    run.RunNumber,
                    run.ExpiredBatchesCount,
                    run.TotalQuantity,
                    run.TotalValue,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    private static Task AuditAsync(
        IAuditWriter audit,
        string action,
        LocationId locationId,
        DocumentNumber runNumber,
        int batchCount,
        decimal totalQuantity,
        decimal totalValue,
        CancellationToken cancellationToken)
        => audit.WriteAsync(
            new AuditEntry(
                action,
                EntityType: nameof(ExpiryRunResult),
                EntityId: null,
                NewValueJson: JsonSerializer.Serialize(new
                {
                    RunNumber = runNumber.Value,
                    ExpiredBatchesCount = batchCount,
                    TotalQuantity = totalQuantity,
                    TotalValue = totalValue,
                }),
                LocationId: locationId),
            cancellationToken);
}
