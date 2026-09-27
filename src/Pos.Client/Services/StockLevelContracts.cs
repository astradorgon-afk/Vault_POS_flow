namespace Pos.Client.Services;

/// <summary>A location's stock, product by product, as head office reports it.</summary>
/// <param name="LocationId">The location.</param>
/// <param name="Code">Its short code.</param>
/// <param name="Name">Its name.</param>
/// <param name="SalesRateDays">How many recent days the sales rate averages over.</param>
/// <param name="Products">Every stocked product.</param>
public sealed record PosStockLevelReport(
    Guid LocationId,
    string Code,
    string Name,
    int SalesRateDays,
    IReadOnlyList<PosStockLevel> Products);

/// <summary>One product's stock at a location.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="Name">Its name.</param>
/// <param name="Category">Its category.</param>
/// <param name="Available">Sellable quantity on hand.</param>
/// <param name="InTransit">Quantity on its way here.</param>
/// <param name="OnHold">Quantity held back (quarantine, damaged, expired, awaiting inspection).</param>
/// <param name="StockValue">The cost value of the sellable stock.</param>
/// <param name="MinimumStock">The minimum level, when set.</param>
/// <param name="ReorderPoint">The level at which to reorder, when set.</param>
/// <param name="TargetStock">The level to restock to, when set.</param>
/// <param name="MaximumStock">The most it should hold, when set.</param>
/// <param name="DailySales">Average units sold per day recently.</param>
/// <param name="DaysOfCover">How many days the stock lasts at that rate; null when not selling.</param>
/// <param name="Status">Out, Low, Healthy or Over.</param>
/// <param name="LastSoldOn">The business date it last sold at this location; null when it never has.</param>
public sealed record PosStockLevel(
    Guid ProductId,
    string Sku,
    string Name,
    string Category,
    decimal Available,
    decimal InTransit,
    decimal OnHold,
    decimal StockValue,
    decimal? MinimumStock,
    decimal? ReorderPoint,
    decimal? TargetStock,
    decimal? MaximumStock,
    decimal DailySales,
    decimal? DaysOfCover,
    string Status,
    DateOnly? LastSoldOn = null);
