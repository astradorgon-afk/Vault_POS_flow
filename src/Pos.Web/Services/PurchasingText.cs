using System.Globalization;

namespace Pos.Web.Services;

/// <summary>How purchase order states read on screen, shared by the purchasing pages.</summary>
public static class PurchasingText
{
    /// <summary>The stages an order moves through, in order, for the progress bar.</summary>
    public static readonly string[] Stages = ["Draft", "PendingApproval", "Approved", "Ordered", "FullyReceived", "Closed"];

    /// <summary>Gets the plain-language name of a status.</summary>
    public static string Label(string status) => status switch
    {
        "Draft" => "Draft",
        "PendingApproval" => "Awaiting approval",
        "Approved" => "Approved",
        "Rejected" => "Rejected",
        "Ordered" => "Ordered",
        "PartiallyReceived" => "Incomplete",
        "FullyReceived" => "Received",
        "Closed" => "Closed",
        "Cancelled" => "Cancelled",
        _ => status,
    };

    /// <summary>
    /// Gets the name of a status, marking an order closed before everything
    /// good arrived as incomplete rather than letting it read as done.
    /// </summary>
    public static string Label(string status, decimal outstanding)
        => status == "Closed" && outstanding > 0 ? "Closed · incomplete" : Label(status);

    /// <summary>Gets the chip colour, with closed-short orders shown as a problem.</summary>
    public static string Tone(string status, decimal outstanding)
        => status == "Closed" && outstanding > 0 ? "bad" : Tone(status);

    /// <summary>Gets whether the supplier still owes goods on an order that has stopped moving forward.</summary>
    public static bool IsIncomplete(string status, decimal outstanding)
        => status == "PartiallyReceived" || (status == "Closed" && outstanding > 0);

    /// <summary>Gets the colour family for a status chip.</summary>
    public static string Tone(string status) => status switch
    {
        "Draft" => "muted",
        "PendingApproval" => "warn",
        "Approved" or "Ordered" => "info",
        "PartiallyReceived" => "warn",
        "FullyReceived" or "Closed" => "ok",
        "Rejected" or "Cancelled" => "bad",
        _ => "muted",
    };

    /// <summary>Gets how far along the happy path a status is, for the progress bar.</summary>
    public static int StageIndex(string status) => status switch
    {
        "Draft" => 0,
        "PendingApproval" => 1,
        "Approved" => 2,
        "Ordered" or "PartiallyReceived" => 3,
        "FullyReceived" => 4,
        "Closed" => 5,
        _ => -1,
    };

    /// <summary>Formats an amount in pesos.</summary>
    public static string Money(decimal amount)
        => amount.ToString("₱#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>Formats a quantity without trailing zeros.</summary>
    public static string Quantity(decimal quantity)
        => quantity.ToString("#,##0.###", CultureInfo.InvariantCulture);

    /// <summary>Names an order for people: its number once issued, otherwise a draft label.</summary>
    public static string OrderName(string? number) => string.IsNullOrWhiteSpace(number) ? "Draft order" : number;
}
