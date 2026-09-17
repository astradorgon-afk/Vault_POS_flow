using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Writes a strongly-typed identifier as the bare GUID it wraps, and reads one
/// back.
/// </summary>
/// <remarks>
/// <para>
/// Without this every identifier in a change-feed payload goes on the wire as
/// <c>{"value":"..."}</c>, because the ids are single-property records and that
/// is what the serializer does with one. A device would then need a matching
/// shape to read them, and the feed's JSON would be full of wrappers that mean
/// nothing to anybody reading it.
/// </para>
/// <para>
/// A factory rather than one converter per type: there are around forty
/// identifiers, and the one thing worse than forty converters is thirty-nine.
/// </para>
/// </remarks>
public sealed class StronglyTypedIdJsonConverter : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeof(IStronglyTypedId).IsAssignableFrom(typeToConvert)
               && typeToConvert.GetConstructor([typeof(Guid)]) is not null;
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(IdConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class IdConverter<TId> : JsonConverter<TId>
        where TId : struct, IStronglyTypedId
    {
        private static readonly ConstructorInfo FromGuid = typeof(TId).GetConstructor([typeof(Guid)])!;

        public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => (TId)FromGuid.Invoke([reader.GetGuid()]);

        public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteStringValue(value.Value);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WritePropertyName(value.Value.ToString());
        }

        public override TId ReadAsPropertyName(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => (TId)FromGuid.Invoke([Guid.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture)]);
    }
}
