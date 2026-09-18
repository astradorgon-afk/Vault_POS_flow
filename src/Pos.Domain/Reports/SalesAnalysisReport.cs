using System.Text.Json.Serialization;

namespace Pos.Domain.Reports;

/// <summary>What a sales analysis is cut by.</summary>
/// <remarks>
/// Travels by name. A client reading <c>3</c> and having to know it means the
/// store cut is how a renumbering silently changes what a saved report shows.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SalesAnalysisGrouping>))]
public enum SalesAnalysisGrouping
{
    /// <summary>One row per product sold.</summary>
    Product = 1,

    /// <summary>One row per product category.</summary>
    Category = 2,

    /// <summary>One row per store.</summary>
    Location = 3,

    /// <summary>One row per cashier who rang the sale up.</summary>
    Cashier = 4,
}

/// <summary>
/// One line of a sales analysis.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="Revenue"/> is <b>net of VAT</b>, because margin measured on a
/// tax-inclusive price flatters every figure by the tax the business never keeps.
/// Prices here are tax-inclusive (POS.md §2.6), so it is the line's net amount
/// less the VAT collected on it — not the line's taxable base, which is
/// deliberately zero on an exempt line and would report everything a pharmacy
/// sells to a senior citizen as having earned nothing.
/// </para>
/// <para>
/// <paramref name="QuantityReturned"/> is the only figure here that moves after
/// the fact. Everything else is what was rung up during the period and never
/// changes, so a report run again next month still reconciles with the one
/// somebody printed. Returns are deliberately **not** netted out of the period
/// that sold the goods: doing so would rewrite a past report every time a
/// customer walked back in, and would put a line's revenue and its reversal in
/// different reports.
/// </para>
/// </remarks>
/// <param name="GroupId">The product, category, location or cashier.</param>
/// <param name="GroupCode">Its code — SKU, category code, store code or employee code.</param>
/// <param name="GroupName">Its name, as somebody reading the report would say it.</param>
/// <param name="SaleCount">How many completed sales contributed.</param>
/// <param name="QuantitySold">Units rung up.</param>
/// <param name="QuantityReturned">How many of those units have since come back.</param>
/// <param name="GrossRevenue">What the customer was charged, VAT included, before discount.</param>
/// <param name="Discount">What was taken off.</param>
/// <param name="Revenue">What was earned, net of VAT and after discount.</param>
/// <param name="Cost">The cost of the units sold, at the cost recorded on the line.</param>
/// <param name="GrossProfit">Revenue less cost.</param>
/// <param name="MarginPercent">Gross profit as a percentage of revenue.</param>
public sealed record SalesAnalysisRow(
    Guid GroupId,
    string GroupCode,
    string GroupName,
    int SaleCount,
    decimal QuantitySold,
    decimal QuantityReturned,
    decimal GrossRevenue,
    decimal Discount,
    decimal Revenue,
    decimal Cost,
    decimal GrossProfit,
    decimal MarginPercent);

/// <summary>A sales analysis over a period.</summary>
/// <param name="FromDate">The first business date included.</param>
/// <param name="ToDate">The last business date included.</param>
/// <param name="GroupBy">What the rows are cut by.</param>
/// <param name="Totals">The whole period, on the same basis as the rows.</param>
/// <param name="Rows">The rows, largest revenue first.</param>
/// <param name="Truncated">
/// Whether more rows matched than were returned. Said out loud rather than left
/// for the reader to infer from a suspiciously round count, because a report
/// silently missing its tail is one somebody will act on.
/// </param>
public sealed record SalesAnalysisReport(
    DateOnly FromDate,
    DateOnly ToDate,
    SalesAnalysisGrouping GroupBy,
    SalesAnalysisRow Totals,
    IReadOnlyList<SalesAnalysisRow> Rows,
    bool Truncated);

/// <summary>What was taken, by how it was taken, over a period.</summary>
/// <param name="Method">The payment method.</param>
/// <param name="PaymentCount">How many payments.</param>
/// <param name="Amount">What was settled by it.</param>
/// <param name="Change">The change given back, where the method gives change.</param>
public sealed record SalesPaymentMethodRow(
    string Method,
    int PaymentCount,
    decimal Amount,
    decimal Change);
