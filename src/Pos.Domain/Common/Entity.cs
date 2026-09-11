namespace Pos.Domain.Common;

/// <summary>
/// Base class for entities: equality is identity, never structure.
/// </summary>
/// <typeparam name="TId">The strongly typed identifier.</typeparam>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct, IEquatable<TId>
{
    /// <summary>Gets the entity identifier.</summary>
    public TId Id { get; protected init; }

    /// <inheritdoc />
    public bool Equals(Entity<TId>? other)
        => other is not null
           && other.GetType() == GetType()
           && other.Id.Equals(Id);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    /// <summary>Determines whether two entities have the same identity.</summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>Determines whether two entities have different identities.</summary>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
