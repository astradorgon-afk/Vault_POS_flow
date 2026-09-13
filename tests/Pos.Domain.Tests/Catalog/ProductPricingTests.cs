using FluentAssertions;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Domain.Tests.Catalog;

/// <summary>
/// Effective-dated price scheduling (ADR-0029).
/// </summary>
/// <remarks>
/// A price never starts in the past. A new price that overlaps exactly one
/// earlier-starting price takes over from it by closing its end; a temporary
/// price inside a longer one hands the old amount back in a continuation row.
/// Any other overlap is refused, because it would silently cancel a price someone
/// else scheduled. Periods are half-open, as the database compares them.
/// </remarks>
public sealed class ProductPricingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly UserId Manager = UserId.New();
    private static readonly LocationId Store = LocationId.New();

    [Fact]
    public void FirstPrice_IsAddedAsIs()
    {
        Product product = NewProduct();

        Result<ProductPriceId> scheduled = product.SchedulePrice(null, 50m, Now, null, Manager, "Launch", Now);

        scheduled.IsSuccess.Should().BeTrue();
        product.Prices.Should().ContainSingle()
            .Which.Should().Match<ProductPrice>(p =>
                p.Id == scheduled.Value && p.Amount == 50m && p.EffectiveFromUtc == Now && p.EffectiveToUtc == null);
    }

    [Fact]
    public void OpenEndedPrice_SupersedesTheOpenEndedPredecessor_ByClosingItsEnd()
    {
        Product product = NewProduct();
        product.SchedulePrice(null, 50m, Now, null, Manager, "Launch", Now);

        Result<ProductPriceId> scheduled = product.SchedulePrice(null, 55m, Now.AddDays(7), null, Manager, "Increase", Now);

        scheduled.IsSuccess.Should().BeTrue();
        Periods(product).Should().Equal(
            (50m, Now, Now.AddDays(7)),
            (55m, Now.AddDays(7), (DateTimeOffset?)null));
    }

    [Fact]
    public void TemporaryPrice_InsideAnOpenEndedPrice_ResumesTheOldAmountAfterwards()
    {
        Product product = NewProduct();
        product.SchedulePrice(null, 50m, Now, null, Manager, "Launch", Now);

        Result<ProductPriceId> promo = product.SchedulePrice(
            null, 40m, Now.AddDays(2), Now.AddDays(4), Manager, "Weekend promo", Now);

        promo.IsSuccess.Should().BeTrue();
        Periods(product).Should().Equal(
            (50m, Now, Now.AddDays(2)),
            (40m, Now.AddDays(2), Now.AddDays(4)),
            (50m, Now.AddDays(4), (DateTimeOffset?)null));
    }

    [Fact]
    public void TemporaryPrice_InsideABoundedPrice_ResumesUntilTheOriginalEnd()
    {
        Product product = NewProduct();
        product.SchedulePrice(null, 50m, Now, null, Manager, "Launch", Now);
        product.SchedulePrice(null, 60m, Now.AddDays(10), null, Manager, "Increase", Now);

        Result<ProductPriceId> promo = product.SchedulePrice(
            null, 45m, Now.AddDays(3), Now.AddDays(5), Manager, "Promo", Now);

        promo.IsSuccess.Should().BeTrue();
        Periods(product).Should().Equal(
            (50m, Now, Now.AddDays(3)),
            (45m, Now.AddDays(3), Now.AddDays(5)),
            (50m, Now.AddDays(5), Now.AddDays(10)),
            (60m, Now.AddDays(10), (DateTimeOffset?)null));
    }

    [Fact]
    public void PriceSpanningTwoScheduledPrices_IsRefused_AndNothingChanges()
    {
        Product product = NewProduct();
        product.SchedulePrice(null, 50m, Now, null, Manager, "Launch", Now);
        product.SchedulePrice(null, 60m, Now.AddDays(10), null, Manager, "Increase", Now);
        List<(decimal Amount, DateTimeOffset From, DateTimeOffset? To)> before = Periods(product);

        Result<ProductPriceId> scheduled = product.SchedulePrice(null, 55m, Now.AddDays(5), null, Manager, "Clash", Now);

        scheduled.Errors.Should().ContainSingle(e => e.Code == "catalog.price_overlap");
        Periods(product).Should().Equal(before);
    }

    [Fact]
    public void PriceStartingWithOrBeforeAScheduledPrice_IsRefused()
    {
        Product product = NewProduct();
        product.SchedulePrice(null, 60m, Now.AddDays(10), null, Manager, "Future price", Now);

        product.SchedulePrice(null, 55m, Now.AddDays(10), null, Manager, "Same start", Now)
            .Errors.Should().ContainSingle(e => e.Code == "catalog.price_overlap");

        product.SchedulePrice(null, 55m, Now.AddDays(5), null, Manager, "Earlier start", Now)
            .Errors.Should().ContainSingle(e => e.Code == "catalog.price_overlap");
    }

    [Fact]
    public void AdjacentPeriods_DoNotOverlap()
    {
        Product product = NewProduct();
        product.SchedulePrice(null, 40m, Now, Now.AddDays(1), Manager, "Opening promo", Now);

        Result<ProductPriceId> next = product.SchedulePrice(null, 50m, Now.AddDays(1), null, Manager, "Regular", Now);

        next.IsSuccess.Should().BeTrue();
        Periods(product).Should().Equal(
            (40m, Now, Now.AddDays(1)),
            (50m, Now.AddDays(1), (DateTimeOffset?)null));
    }

    [Fact]
    public void BackdatedPrice_IsRefused_ButClockSkewIsTolerated()
    {
        Product product = NewProduct();

        product.SchedulePrice(null, 50m, Now.AddHours(-1), null, Manager, "Backdated", Now)
            .Errors.Should().ContainSingle(e => e.Code == "catalog.price_backdated");

        product.SchedulePrice(null, 50m, Now.AddMinutes(-2), null, Manager, "Clicked a moment ago", Now)
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void InvalidAmountsAndPeriods_AreRefused()
    {
        Product product = NewProduct();

        product.SchedulePrice(null, -1m, Now, null, Manager, "Negative", Now)
            .Errors.Should().ContainSingle(e => e.Code == "product.price_negative");

        product.SchedulePrice(null, 50m, Now, Now, Manager, "Empty period", Now)
            .Errors.Should().ContainSingle(e => e.Code == "product.price_effective_to");

        product.Prices.Should().BeEmpty();
    }

    [Fact]
    public void LocationPrices_AreScopedIndependently_AndFallBackToTheOrganizationPrice()
    {
        Product product = NewProduct();
        product.SchedulePrice(null, 50m, Now, null, Manager, "Everywhere", Now);

        product.SchedulePrice(Store, 48m, Now.AddDays(1), null, Manager, "Store match", Now)
            .IsSuccess.Should().BeTrue();

        product.Prices.Should().HaveCount(2, "a store price does not close the organization-wide price");
        product.PriceAt(Store, Now.AddHours(1))!.Amount.Should().Be(50m);
        product.PriceAt(Store, Now.AddDays(2))!.Amount.Should().Be(48m);
        product.PriceAt(LocationId.New(), Now.AddDays(2))!.Amount.Should().Be(50m);
        product.PriceAt(null, Now.AddMinutes(-1)).Should().BeNull();
    }

    private static List<(decimal Amount, DateTimeOffset From, DateTimeOffset? To)> Periods(Product product)
        => [.. product.Prices
            .Where(p => p.LocationId == null)
            .OrderBy(p => p.EffectiveFromUtc)
            .Select(p => (p.Amount, p.EffectiveFromUtc, p.EffectiveToUtc))];

    private static Product NewProduct()
        => Product.Create("PRICE-01", "Priced product", CategoryId.New(), UnitOfMeasureId.New(), Manager).Value;
}
