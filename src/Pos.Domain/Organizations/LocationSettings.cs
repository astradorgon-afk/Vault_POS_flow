using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Domain.Inventory;

namespace Pos.Domain.Organizations;

/// <summary>
/// Operational settings for a location, including the negative-stock policy the
/// ledger must honour.
/// </summary>
/// <remarks>
/// <para>
/// Stored as JSON on the location row. A location whose settings have not been
/// loaded must be treated as the strictest configuration, so defaults are the
/// conservative choice everywhere and only explicitly recorded values change
/// behaviour.
/// </para>
/// </remarks>
public sealed record LocationSettings
{
    /// <summary>Maximum length of a receipt header or footer line.</summary>
    public const int ReceiptTextMaxLength = 120;

    /// <summary>
    /// The conservative defaults: negative stock prohibited, direct supplier
    /// delivery disallowed, a 72-hour offline grace period and empty receipt text.
    /// </summary>
    public static LocationSettings Default { get; } = new();

    /// <summary>Gets or sets how this location handles operations that would drive a bucket below zero.</summary>
    public NegativeStockPolicy NegativeStockPolicy { get; init; } = NegativeStockPolicy.Prohibit;

    /// <summary>Gets or sets whether a supplier may deliver direct to this location without a transfer.</summary>
    public bool AllowsDirectSupplierDelivery { get; init; }

    /// <summary>Gets or sets how long an offline device may keep selling before its batch is flagged.</summary>
    public TimeSpan OfflineGracePeriod { get; init; } = TimeSpan.FromHours(72);

    /// <summary>Gets or sets the receipt header text.</summary>
    public string ReceiptHeader { get; init; } = string.Empty;

    /// <summary>Gets or sets the receipt footer text.</summary>
    public string ReceiptFooter { get; init; } = string.Empty;

    /// <summary>Serializes to JSON for storage.</summary>
    public string ToJson()
        => JsonSerializer.Serialize(this, _serializerOptions);

    /// <summary>Deserializes settings stored as JSON.</summary>
    /// <param name="json">The stored JSON, or <see langword="null"/> for defaults.</param>
    /// <returns>The loaded settings, or <see cref="Default"/> when unset.</returns>
    public static LocationSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            return JsonSerializer.Deserialize<LocationSettings>(json, _serializerOptions) ?? Default;
        }
        catch (JsonException)
        {
            // A corrupt settings blob must never drop a store into a more
            // permissive policy than its owner configured. Fail towards the
            // strictest option and let an operator notice the damage.
            return Default;
        }
    }

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };
}