using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// An effective-dated, location-scoped selling price. Prices never change in
/// place; a new price row supersedes the old one on its effective date, and the
/// database enforces that no two price rows for the same product and scope
/// overlap in time.
/// </summary>
public sealed class ProductPrice
{
    internal ProductPrice(
        ProductPriceId id,
        ProductId productId,
        LocationId? locationId,
        Money money,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc,
        UserId createdByUserId,
        string? reason)
    {
        Id = id;
        ProductId = productId;
        LocationId = locationId;
        Price = money;
        EffectiveFromUtc = effectiveFromUtc;
        EffectiveToUtc = effectiveToUtc;
        CreatedByUserId = createdByUserId;
        Reason = reason;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private ProductPrice()
        => Reason = string.Empty;

    /// <summary>Gets the price row identifier.</summary>
    public ProductPriceId Id { get; }

    /// <summary>Gets the owning product.</summary>
    public ProductId ProductId { get; }

    /// <summary>Gets the location this price applies to, or null for every location.</summary>
    public LocationId? LocationId { get; }

    /// <summary>Gets the amount.</summary>
    public Money Price { get; }

    /// <summary>Gets the plain amount, for persistence.</summary>
    public decimal Amount => Price.Amount;

    /// <summary>Gets when the price starts applying, inclusive.</summary>
    public DateTimeOffset EffectiveFromUtc { get; }

    /// <summary>Gets when the price period ends, exclusive, or null for no end.</summary>
    public DateTimeOffset? EffectiveToUtc { get; }

    /// <summary>Gets who set the price.</summary>
    public UserId CreatedByUserId { get; }

    /// <summary>Gets the reason recorded with the change.</summary>
    public string? Reason { get; }

    /// <summary>Gets when the row was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Determines whether this price row overlaps a candidate period.</summary>
    /// <param name="from">The candidate start.</param>
    /// <param name="to">The candidate end, or null for no end.</param>
    /// <returns><see langword="true"/> when the periods overlap.</returns>
    public bool Overlaps(DateTimeOffset from, DateTimeOffset? to)
        => from <= (EffectiveToUtc ?? DateTimeOffset.MaxValue)
           && (EffectiveFromUtc <= (to ?? DateTimeOffset.MaxValue));
}