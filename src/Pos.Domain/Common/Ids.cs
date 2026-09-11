namespace Pos.Domain.Common;

/// <summary>
/// Marks a strongly typed identifier so infrastructure can discover and map
/// them generically.
/// </summary>
public interface IStronglyTypedId
{
    /// <summary>Gets the underlying value.</summary>
    Guid Value { get; }
}
