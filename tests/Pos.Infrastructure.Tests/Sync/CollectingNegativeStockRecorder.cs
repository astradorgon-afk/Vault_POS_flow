using Pos.Application.Common.Abstractions;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Tests.Sync;

/// <summary>
/// Holds refused draws in memory for the sync fixtures.
/// </summary>
/// <remarks>
/// The real recorder writes through a scope of its own, which these fixtures do
/// not have. Nothing here posts a sale, so nothing is ever collected — what the
/// processor needs is something to ask, and asking is all it does.
/// </remarks>
internal sealed class CollectingNegativeStockRecorder : INegativeStockAttemptRecorder
{
    private readonly List<NegativeStockAttempt> pending = [];

    public bool HasPending => this.pending.Count > 0;

    public IReadOnlyList<NegativeStockAttempt> Pending => this.pending;

    public void Record(NegativeStockAttempt attempt) => this.pending.Add(attempt);

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        this.pending.Clear();
        return Task.CompletedTask;
    }
}
