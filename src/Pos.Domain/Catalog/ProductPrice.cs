using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// An effective-dated, location-scoped selling price. A price's amount and start
/// never change; a new price row supersedes the old one on its effective date by
/// closing the old row's open end (ADR-0029), and the database enforces that no
/// two price rows for the same product and scope overlap in time.
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
    public DateTimeOffset? EffectiveToUtc { get; private set; }

    /// <summary>Gets who set the price.</summary>
    public UserId CreatedByUserId { get; }

    /// <summary>Gets the reason recorded with the change.</summary>
    public string? Reason { get; }

    /// <summary>Gets when the row was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// Determines whether this price row overlaps a candidate period. Both periods
    /// are half-open, <c>[from, to)</c>, exactly as the database exclusion
    /// constraint compares them, so a price ending at T and one starting at T do
    /// not overlap.
    /// </summary>
    /// <param name="from">The candidate start, inclusive.</param>
    /// <param name="to">The candidate end, exclusive, or null for no end.</param>
    /// <returns><see langword="true"/> when the periods overlap.</returns>
    public bool Overlaps(DateTimeOffset from, DateTimeOffset? to)
        => from < (EffectiveToUtc ?? DateTimeOffset.MaxValue)
           && EffectiveFromUtc < (to ?? DateTimeOffset.MaxValue);

    /// <summary>
    /// Ends the period at a later instant, or reopens it. Only the scheduling
    /// rule on <see cref="Product"/> calls this: to hand the period over to the
    /// price that supersedes it, or to carry the predecessor through a cancelled
    /// price's period. The amount and the start never change.
    /// </summary>
    /// <param name="endUtc">The new exclusive end; after the start. A null value
    /// reopens the period (no scheduled end).</param>
    internal void Close(DateTimeOffset? endUtc) => EffectiveToUtc = endUtc;
}