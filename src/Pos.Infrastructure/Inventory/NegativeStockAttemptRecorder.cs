using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Auditing;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Inventory;

/// <summary>
/// Collects refused stock draws for one request and writes them, with an audit
/// entry each, through a context of their own.
/// </summary>
/// <remarks>
/// A separate scope, and therefore a separate context and connection, is the
/// point: the request's context still tracks everything the refused command
/// staged before it rolled back, and saving through it would commit that too.
/// </remarks>
/// <param name="scopes">Creates the scope the attempts are written through.</param>
public sealed class NegativeStockAttemptRecorder(IServiceScopeFactory scopes) : INegativeStockAttemptRecorder
{
    private readonly List<NegativeStockAttempt> _pending = [];

    /// <inheritdoc />
    public bool HasPending => _pending.Count > 0;

    /// <inheritdoc />
    public IReadOnlyList<NegativeStockAttempt> Pending => _pending;

    /// <inheritdoc />
    public void Record(NegativeStockAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        // The ledger may stage the same event more than once in a request (for
        // example a caller that retries); one refused event and bucket is one attempt.
        bool known = _pending.Any(p =>
            p.EventId == attempt.EventId
            && p.LocationId == attempt.LocationId
            && p.ProductId == attempt.ProductId
            && p.BatchKey == attempt.BatchKey
            && p.State == attempt.State);

        if (!known)
        {
            _pending.Add(attempt);
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        NegativeStockAttempt[] attempts = [.. _pending];
        _pending.Clear();

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        PosDbContext context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        IAuditWriter audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

        foreach (NegativeStockAttempt attempt in attempts)
        {
            context.NegativeStockAttempts.Add(attempt);

            await audit.WriteAsync(
                    new AuditEntry(
                        AuditActions.Inventory.NegativeStockAttempted,
                        nameof(NegativeStockAttempt),
                        attempt.Id.Value,
                        NewValueJson: JsonSerializer.Serialize(new
                        {
                            ProductId = attempt.ProductId.Value,
                            BatchKey = attempt.BatchKey.Value,
                            State = attempt.State.ToString(),
                            MovementType = attempt.MovementType.ToString(),
                            attempt.RequestedQuantity,
                            attempt.AvailableQuantity,
                            Policy = attempt.Policy.ToString(),
                            attempt.ReferenceNumber,
                        }),
                        ReferenceDocumentType: attempt.ReferenceDocumentType,
                        ReferenceDocumentId: attempt.ReferenceDocumentId,
                        LocationId: attempt.LocationId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
