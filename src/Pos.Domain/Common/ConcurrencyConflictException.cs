namespace Pos.Domain.Common;

/// <summary>
/// Thrown when an optimistic-concurrency check fails at the storage boundary.
/// </summary>
/// <remarks>
/// The unit of work translates the provider's concrete exception into this
/// domain type so the application layer can map contention to a failed
/// <see cref="Result"/> without depending on the storage provider. Today every
/// concurrency-token-bearing row is an inventory balance, so the application
/// layer maps this to <see cref="Pos.Domain.Inventory.InventoryErrors.BalanceContention"/>;
/// a second kind of entity with a concurrency token should carry its own error.
/// </remarks>
public sealed class ConcurrencyConflictException(Exception? inner = null)
    : Exception("A concurrent writer changed a record while this operation was in flight.", inner);