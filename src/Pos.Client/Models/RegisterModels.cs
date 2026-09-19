using System.Globalization;
using Pos.Client.Services;
using Pos.Domain.Sales;

namespace Pos.Client.Models;

/// <summary>One line on the register's current sale.</summary>
public sealed class SaleCartLine(CatalogueItem item, int quantity = 1)
{
    /// <summary>The product being rung up.</summary>
    public CatalogueItem Item { get; } = item;

    /// <summary>The quantity being sold.</summary>
    public int Quantity { get; set; } = quantity;

    /// <summary>The line total at the price head office supplied.</summary>
    public decimal Total => Item.Price!.Value * Quantity;

    /// <summary>Gets a value indicating whether this product can be rung up at all.</summary>
    public bool IsSellable => Item.Price is not null && Item.BaseUnitOfMeasureId is not null;
}

/// <summary>A transaction set aside at the till (hold / suspend).</summary>
/// <remarks>
/// Held sales live for the current signed-in session only. The register's local
/// store keeps no table for them yet, so restarting the register or locking it
/// loses them.</remarks>
/// <param name="Id">A till-scoped identifier such as #H001.</param>
/// <param name="CreatedAtUtc">When the cashier held it.</param>
/// <param name="Lines">A snapshot of the cart at hold time.</param>
public sealed record HeldSale(
    string Id,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<SaleCartLine> Lines)
{
    /// <summary>Gets the number of physical items in the held sale.</summary>
    public int ItemCount => Lines.Sum(line => line.Quantity);

    /// <summary>Gets the held sale's total.</summary>
    public decimal Total => Lines.Sum(line => line.Total);
}

/// <summary>A tender the payment pane has approved, ready for the server sale.</summary>
/// <param name="Method">The payment rail.</param>
/// <param name="Amount">The amount applied to the sale.</param>
/// <param name="Tendered">Cash handed over, when the rail is cash.</param>
/// <param name="ProviderReference">Card or wallet reference, when supplied.</param>
public sealed record RegisterPaymentInput(
    PaymentMethod Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference);

/// <summary>Money shown on the register, in the store's currency.</summary>
public static class MoneyFormat
{
    /// <summary>Formats an amount in Philippine peso currency.</summary>
    public static string Php(decimal value)
        => value.ToString("C", CultureInfo.GetCultureInfo("en-PH"));

    /// <summary>Reads an amount a cashier typed (commas, dots, ₱ accepted).</summary>
    public static bool TryParseAmount(string? value, out decimal amount)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);

    /// <summary>Formats a time for the compact status strip.</summary>
    public static string Time(DateTimeOffset? utc)
        => utc is { } value ? value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "—";
}