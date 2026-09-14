using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Domain.Inventory;

namespace Pos.Domain.Organizations;

/// <summary>
/// Operational settings for a location: the negative-stock policy the ledger
/// must honour, the VAT rate and cash rounding for sales, and the cash-shift
/// policies that govern opening and closing the drawer.
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

    /// <summary>Default value-added tax rate as a fraction (12% under current Philippine rules).</summary>
    public const decimal DefaultVatRate = 0.12m;

    /// <summary>Default cash-rounding increment for change.</summary>
    public const decimal DefaultCashRoundingIncrement = 0.01m;

    /// <summary>
    /// The conservative defaults: negative stock prohibited, direct supplier
    /// delivery disallowed, a 72-hour offline grace period, empty receipt text
    /// and zero cash-variance tolerance.
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

    /// <summary>
    /// Gets or sets the value-added tax rate as a fraction of the gross price
    /// (0.12 for twelve percent). The server deducts VAT from the tax-inclusive
    /// sale price using this rate (POS.md §2.6).
    /// </summary>
    public decimal VatRate { get; init; } = DefaultVatRate;

    /// <summary>
    /// Gets or sets the increment cash change is rounded to, for example 0.05
    /// rounds change to the nearest five centavos (POS.md §2.7). A positive
    /// amount; the strictest configuration does not round at all.
    /// </summary>
    public decimal CashRoundingIncrement { get; init; } = DefaultCashRoundingIncrement;

    /// <summary>
    /// Gets or sets the number of days before expiry at which a batch is
    /// considered "expiring soon" for warnings and alerts. The default of
    /// 90 days matches common pharmaceutical and perishable-goods thresholds.
    /// </summary>
    /// <remarks>
    /// The worker moves past-expiry batches regardless of this setting. This
    /// threshold controls the warning band used by alerts and, eventually, the
    /// sale-blocking logic in POS.
    /// </remarks>
    public int ExpiryWarningDays { get; init; } = 90;

    /// <summary>
    /// Gets or sets the cash-variance tolerance for shift closure, in the
    /// settlement currency. A closed shift whose variance exceeds this amount
    /// requires manager review (and a reason) before it can be reconciled
    /// (POS.md §1). The strictest configuration tolerates no variance at all.
    /// </summary>
    public decimal CashVarianceThreshold { get; init; }

    /// <summary>
    /// Gets or sets how long a cashier shift may stay open before a worker
    /// force-closes it and flags the shift for review (POS.md §1). The default
    /// of sixteen hours covers a double shift.
    /// </summary>
    public TimeSpan MaxShiftHours { get; init; } = TimeSpan.FromHours(16);

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