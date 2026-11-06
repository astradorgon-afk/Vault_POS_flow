using FluentAssertions;
using Pos.Domain.Catalog;
using Pos.Infrastructure.Identity;

namespace Pos.Infrastructure.Tests.Identity;

/// <summary>
/// The development demo business must be internally consistent: a catalogue a
/// real store could carry, every product valid for the domain, and nothing the
/// seeder or the demo trading tool would trip over.
/// </summary>
public sealed class DevelopmentCatalogueTests
{
    private static IReadOnlyList<DevelopmentProduct> Products => DevelopmentCatalogue.Products;

    [Fact]
    public void TheCatalogue_IsTheSizeOfARealGrocery()
        => Products.Count.Should().BeInRange(600, 1500);

    [Fact]
    public void EverySku_IsUniqueAndValid()
    {
        Products.Select(p => p.Sku).Should().OnlyHaveUniqueItems();
        Products.Should().OnlyContain(p => Sku.Create(p.Sku).IsSuccess);
    }

    [Fact]
    public void EveryBarcode_IsAUniqueValidEan13UnderThePhilippinePrefix()
    {
        Products.Select(p => p.Barcode).Should().OnlyHaveUniqueItems();

        foreach (DevelopmentProduct product in Products)
        {
            product.Barcode.Should().MatchRegex("^480[0-9]{10}$", product.Sku);
            int sum = 0;
            for (int i = 0; i < 12; i++)
            {
                int digit = product.Barcode[i] - '0';
                sum += i % 2 == 0 ? digit : digit * 3;
            }

            (product.Barcode[12] - '0').Should().Be((10 - (sum % 10)) % 10, product.Barcode);
        }
    }

    [Fact]
    public void EveryProduct_SellsAboveItsCost()
        => Products.Should().OnlyContain(p => p.UnitCost > 0m && p.UnitCost < p.Price && p.DailyUnits > 0m);

    [Fact]
    public void EveryProduct_PointsAtAKnownCategoryAndSupplier()
    {
        HashSet<string> categories = [.. DevelopmentCatalogue.Categories.Select(c => c.Code)];
        HashSet<string> suppliers = [.. DevelopmentCatalogue.Suppliers.Select(s => s.Code)];

        Products.Should().OnlyContain(p => categories.Contains(p.Category) && suppliers.Contains(p.Supplier));
        DevelopmentCatalogue.Categories.Should().OnlyContain(c => Products.Any(p => p.Category == c.Code));
    }

    [Fact]
    public void NamesFitTheDomain_AndReadLikeShelfLabels()
    {
        Products.Should().OnlyContain(p => p.Name.Length <= 128 && !p.Name.Contains("  ", StringComparison.Ordinal));
        Products.Select(p => p.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheCatalogue_IsTheSameOnEveryMachine()
    {
        // A fixed fingerprint of the generated data: a change to the
        // catalogue should be deliberate, and every developer seeds the same.
        DevelopmentProduct first = Products[0];
        first.Sku.Should().Be("RG-1001");
        first.Name.Should().Be("Golden Grain Premium Jasmine Rice 5kg");
        first.Price.Should().Be(345m);
        first.VatExempt.Should().BeTrue();
    }
}
