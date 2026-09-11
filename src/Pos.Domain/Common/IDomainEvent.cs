namespace Pos.Domain.Common;

/// <summary>
/// A fact that has already happened inside an aggregate. Domain events are
/// dispatched only after the transaction that produced them commits, so a
/// handler can never observe a change that was rolled back.
/// </summary>
public interface IDomainEvent
{
    /// <summary>Gets the identifier of this event occurrence.</summary>
    Guid EventId { get; }

    /// <summary>Gets the moment the event occurred, in UTC.</summary>
    DateTimeOffset OccurredAtUtc { get; }
}

/// <summary>Convenience base record supplying identity and timestamp.</summary>
/// <param name="OccurredAtUtc">The moment the event occurred, in UTC.</param>
public abstract record DomainEvent(DateTimeOffset OccurredAtUtc) : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; } = Guid.CreateVersion7();
}
