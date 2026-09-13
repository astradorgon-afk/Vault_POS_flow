using Serilog.Core;
using Serilog.Events;

namespace Pos.Api.Logging;

/// <summary>
/// Masks log properties whose names mark them as secrets, wherever they appear
/// in an event: top-level, inside destructured objects, or inside dictionaries.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the system logs a credential today: the pipeline logs message
/// names, error codes and identifiers, never request bodies. This is the guard
/// against the day someone writes <c>logger.LogInformation("{@Request}", body)</c>
/// on a sign-in body, or an exception carries a connection string.
/// </para>
/// <para>
/// Matching is by property name, normalised to lower case without separators,
/// against whole names and suffixes, so <c>RefreshToken</c> and
/// <c>request.password</c> are masked while <c>PreApprovalTokenId</c> and
/// <c>ErrorCode</c> are not.
/// </para>
/// </remarks>
public sealed class SensitiveDataScrubber : ILogEventEnricher
{
    /// <summary>The value written in place of a masked property.</summary>
    public const string Mask = "***";

    private static readonly string[] SensitiveSuffixes =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "apikey",
        "privatekey",
        "signingkeypem",
        "connectionstring",
        "authorization",
        "cookie",
        "pin",
        "pinhash",
        "passwordhash",
        "twofactorcode",
        "recoverycode",
        "recoverycodes",
        "enrolmentcode",
        "enrollmentcode",
        "sharedkey",
        "authenticatorkey",
    ];

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach ((string name, LogEventPropertyValue value) in logEvent.Properties.ToList())
        {
            LogEventPropertyValue scrubbed = Scrub(name, value);

            if (!ReferenceEquals(scrubbed, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, scrubbed));
            }
        }
    }

    /// <summary>Determines whether a property name marks its value as secret.</summary>
    /// <param name="name">The property name.</param>
    /// <returns><see langword="true"/> when the value must be masked.</returns>
    public static bool IsSensitive(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string normalized = new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

        return SensitiveSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static LogEventPropertyValue Scrub(string name, LogEventPropertyValue value)
    {
        if (IsSensitive(name))
        {
            return new ScalarValue(Mask);
        }

        switch (value)
        {
            case StructureValue structure:
            {
                List<LogEventProperty> properties = [.. structure.Properties
                    .Select(p => new LogEventProperty(p.Name, Scrub(p.Name, p.Value)))];
                bool changed = properties.Where((p, i) => !ReferenceEquals(p.Value, structure.Properties[i].Value)).Any();
                return changed ? new StructureValue(properties, structure.TypeTag) : value;
            }

            case DictionaryValue dictionary:
            {
                List<KeyValuePair<ScalarValue, LogEventPropertyValue>> elements = [.. dictionary.Elements
                    .Select(e => new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        e.Key, Scrub(e.Key.Value?.ToString() ?? string.Empty, e.Value)))];
                bool changed = elements.Zip(dictionary.Elements).Any(pair => !ReferenceEquals(pair.First.Value, pair.Second.Value));
                return changed ? new DictionaryValue(elements) : value;
            }

            case SequenceValue sequence:
            {
                List<LogEventPropertyValue> elements = [.. sequence.Elements.Select(e => Scrub(string.Empty, e))];
                bool changed = elements.Zip(sequence.Elements).Any(pair => !ReferenceEquals(pair.First, pair.Second));
                return changed ? new SequenceValue(elements) : value;
            }

            default:
                return value;
        }
    }
}
