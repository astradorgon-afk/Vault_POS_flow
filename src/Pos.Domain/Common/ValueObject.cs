namespace Pos.Domain.Common;

/// <summary>
/// Base class for value objects that are not naturally expressed as a record:
/// equality is structural, based on <see cref="GetEqualityComponents"/>.
/// </summary>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>Gets the components that define equality for this value.</summary>
    /// <returns>The ordered equality components.</returns>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    /// <inheritdoc />
    public bool Equals(ValueObject? other)
        => other is not null
           && other.GetType() == GetType()
           && other.GetEqualityComponents().SequenceEqual(GetEqualityComponents());

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueObject other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(GetType());
        foreach (object? component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    /// <summary>Determines whether two values are structurally equal.</summary>
    public static bool operator ==(ValueObject? left, ValueObject? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>Determines whether two values are structurally different.</summary>
    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
