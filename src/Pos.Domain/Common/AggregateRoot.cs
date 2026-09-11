namespace Pos.Domain.Common;

/// <summary>
/// An aggregate root: the only entity a repository loads and saves, and the
/// consistency boundary for its invariants.
/// </summary>
/// <typeparam name="TId">The strongly typed identifier.</typeparam>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : struct, IEquatable<TId>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>Gets the events raised since the aggregate was loaded.</summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>Records a domain event for post-commit dispatch.</summary>
    /// <param name="domainEvent">The event to record.</param>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>Clears recorded events after they have been dispatched.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
