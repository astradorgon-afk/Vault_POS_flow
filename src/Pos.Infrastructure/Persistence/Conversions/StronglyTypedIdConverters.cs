using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Persistence.Conversions;

/// <summary>
/// Builds an identifier from its underlying <see cref="Guid"/>.
/// </summary>
/// <typeparam name="TId">The strongly typed identifier.</typeparam>
/// <remarks>
/// The constructor is found once per closed type and compiled to a delegate, so
/// materializing a row costs a delegate call rather than reflection.
/// </remarks>
internal static class StronglyTypedIdFactory<TId>
    where TId : struct, IStronglyTypedId
{
    private static readonly Func<Guid, TId> Factory = Build();

    /// <summary>Creates an identifier from a raw value.</summary>
    /// <param name="value">The underlying value.</param>
    /// <returns>The identifier.</returns>
    public static TId From(Guid value) => Factory(value);

    private static Func<Guid, TId> Build()
    {
        ConstructorInfo constructor = typeof(TId).GetConstructor([typeof(Guid)])
            ?? throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"{typeof(TId).Name} must declare a constructor taking a single Guid to be persisted."));

        ParameterExpression parameter = Expression.Parameter(typeof(Guid), "value");

        return Expression.Lambda<Func<Guid, TId>>(
            Expression.New(constructor, parameter), parameter).Compile();
    }
}

/// <summary>
/// Maps a strongly typed identifier to a plain UUID column, so the domain keeps
/// identifiers distinct while the database stores one familiar type.
/// </summary>
/// <typeparam name="TId">The strongly typed identifier.</typeparam>
public sealed class StronglyTypedIdConverter<TId>()
    : ValueConverter<TId, Guid>(id => id.Value, value => StronglyTypedIdFactory<TId>.From(value))
    where TId : struct, IStronglyTypedId;

/// <summary>Discovers the identifier types that need converters.</summary>
internal static class StronglyTypedIds
{
    /// <summary>Gets every strongly typed identifier type declared in the domain.</summary>
    public static IReadOnlyList<Type> Types { get; } =
    [
        .. typeof(IStronglyTypedId).Assembly.GetTypes()
            .Where(t => t is { IsValueType: true, IsGenericTypeDefinition: false }
                        && typeof(IStronglyTypedId).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal),
    ];

    /// <summary>Gets the closed converter type for an identifier type.</summary>
    /// <param name="idType">The identifier type.</param>
    /// <returns>The converter type.</returns>
    public static Type ConverterFor(Type idType)
        => typeof(StronglyTypedIdConverter<>).MakeGenericType(idType);
}

/// <summary>
/// Stores <see cref="decimal"/> as invariant-culture text.
/// </summary>
/// <remarks>
/// SQLite has no decimal type and would otherwise map to REAL, which is binary
/// floating point. Storing money or quantity as REAL silently corrupts totals,
/// so on SQLite every decimal round-trips through text at a fixed scale.
/// Aggregation of monetary values happens in memory rather than in SQL.
/// </remarks>
/// <param name="scale">The number of decimal places to preserve.</param>
internal sealed class DecimalAsTextConverter(int scale) : ValueConverter<decimal, string>(
    value => value.ToString("0." + new string('0', scale), CultureInfo.InvariantCulture),
    text => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture));

/// <summary>Stores a nullable <see cref="decimal"/> as invariant-culture text.</summary>
/// <param name="scale">The number of decimal places to preserve.</param>
internal sealed class NullableDecimalAsTextConverter(int scale) : ValueConverter<decimal?, string?>(
    value => value == null ? null : value.Value.ToString("0." + new string('0', scale), CultureInfo.InvariantCulture),
    text => text == null ? null : decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture));

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as a round-trippable UTC string.
/// </summary>
/// <remarks>
/// Used on SQLite only; PostgreSQL stores these as <c>timestamptz</c> natively.
/// Values are normalised to UTC on the way in, so a device whose timezone
/// changes cannot retroactively alter recorded times.
/// </remarks>
internal sealed class UtcDateTimeOffsetAsTextConverter() : ValueConverter<DateTimeOffset, string>(
    value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

/// <summary>Stores a nullable <see cref="DateTimeOffset"/> as a round-trippable UTC string.</summary>
internal sealed class NullableUtcDateTimeOffsetAsTextConverter() : ValueConverter<DateTimeOffset?, string?>(
    value => value == null ? null : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    text => text == null
        ? null
        : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
