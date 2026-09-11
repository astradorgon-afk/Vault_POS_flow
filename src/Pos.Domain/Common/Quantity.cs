using System.Globalization;

namespace Pos.Domain.Common;

/// <summary>
/// A stock quantity expressed in a specific unit of measure. Always
/// <see cref="decimal"/>, because weight- and volume-based products are sold in
/// fractions and binary floating point would accumulate error across a ledger.
/// </summary>
/// <remarks>
/// A quantity may be negative: the inventory ledger records signed deltas.
/// Callers that require a positive value use <see cref="IsPositive"/> or the
/// guards on the ledger, rather than the type enforcing it globally.
/// </remarks>
public readonly record struct Quantity : IComparable<Quantity>
{
    /// <summary>Decimal places retained in storage and arithmetic.</summary>
    public const int Scale = 3;

    /// <summary>Initializes a new instance of the <see cref="Quantity"/> struct.</summary>
    /// <param name="value">The amount; rounded to <see cref="Scale"/> places.</param>
    /// <param name="unit">The unit of measure the value is expressed in.</param>
    public Quantity(decimal value, UnitOfMeasureId unit)
    {
        Value = decimal.Round(value, Scale, MidpointRounding.ToEven);
        Unit = unit;
    }

    /// <summary>Gets the amount, at <see cref="Scale"/> decimal places.</summary>
    public decimal Value { get; }

    /// <summary>Gets the unit of measure.</summary>
    public UnitOfMeasureId Unit { get; }

    /// <summary>Gets a value indicating whether the quantity is zero.</summary>
    public bool IsZero => Value == 0m;

    /// <summary>Gets a value indicating whether the quantity is greater than zero.</summary>
    public bool IsPositive => Value > 0m;

    /// <summary>Gets a value indicating whether the quantity is less than zero.</summary>
    public bool IsNegative => Value < 0m;

    /// <summary>Creates a zero quantity in the given unit.</summary>
    /// <param name="unit">The unit of measure.</param>
    /// <returns>Zero quantity.</returns>
    public static Quantity Zero(UnitOfMeasureId unit) => new(0m, unit);

    /// <summary>Returns the absolute value.</summary>
    /// <returns>The absolute quantity.</returns>
    public Quantity Abs() => new(Math.Abs(Value), Unit);

    /// <summary>Adds two quantities in the same unit.</summary>
    public static Quantity operator +(Quantity left, Quantity right)
        => new(left.Value + SameUnit(left, right).Value, left.Unit);

    /// <summary>Subtracts two quantities in the same unit.</summary>
    public static Quantity operator -(Quantity left, Quantity right)
        => new(left.Value - SameUnit(left, right).Value, left.Unit);

    /// <summary>Negates a quantity.</summary>
    public static Quantity operator -(Quantity value) => new(-value.Value, value.Unit);

    /// <summary>Scales a quantity, for example by a pack conversion factor.</summary>
    public static Quantity operator *(Quantity left, decimal factor) => new(left.Value * factor, left.Unit);

    /// <summary>Determines whether the left quantity is less than the right.</summary>
    public static bool operator <(Quantity left, Quantity right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether the left quantity is greater than the right.</summary>
    public static bool operator >(Quantity left, Quantity right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether the left quantity is less than or equal to the right.</summary>
    public static bool operator <=(Quantity left, Quantity right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether the left quantity is greater than or equal to the right.</summary>
    public static bool operator >=(Quantity left, Quantity right) => left.CompareTo(right) >= 0;

    /// <summary>Adds two quantities in the same unit.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The sum.</returns>
    public static Quantity Add(Quantity left, Quantity right) => left + right;

    /// <summary>Subtracts two quantities in the same unit.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The difference.</returns>
    public static Quantity Subtract(Quantity left, Quantity right) => left - right;

    /// <summary>Scales a quantity.</summary>
    /// <param name="left">The quantity.</param>
    /// <param name="factor">The scale factor.</param>
    /// <returns>The scaled quantity.</returns>
    public static Quantity Multiply(Quantity left, decimal factor) => left * factor;

    /// <summary>Negates a quantity.</summary>
    /// <param name="value">The quantity.</param>
    /// <returns>The negated quantity.</returns>
    public static Quantity Negate(Quantity value) => -value;

    /// <inheritdoc />
    public int CompareTo(Quantity other) => Value.CompareTo(SameUnit(this, other).Value);

    /// <inheritdoc />
    public override string ToString()
        => Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static Quantity SameUnit(Quantity left, Quantity right)
    {
        if (left.Unit != right.Unit)
        {
            throw new InvalidOperationException(
                "Cannot combine quantities in different units. Convert through the product unit conversion first.");
        }

        return right;
    }
}
