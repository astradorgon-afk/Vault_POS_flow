using FluentAssertions;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Domain.Tests.Catalog;

/// <summary>
/// Curation of an existing product: master fields, activation, barcodes,
/// per-location stocking, unit conversions and supplier links.
/// </summary>
/// <remarks>
/// A barcode is never deleted or re-pointed: retiring keeps the row and reserves
/// the value, and a product with any active code always has exactly one primary.
/// Location settings and supplier links are edited in place so their
/// one-row-per-pair rule is never at risk.
/// </remarks>
public sealed class ProductCurationTests
{
    private static readonly UserId Manager = UserId.New();
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UpdateDetails_ReplacesEditableFields_AndLeavesTrackingAlone()
    {
        Product product = Product.Create(
            "MILK-01", "Milk", CategoryId.New(), UnitOfMeasureId.New(), Manager,
            tracksExpiry: true, shelfLifeDays: 30).Value;
        CategoryId dairy = CategoryId.New();

        Result updated = product.UpdateDetails(
            "  Fresh Milk 1L ", "Pasteurised", dairy, BrandId.New(), null, "VAT12", false, 62.5m, null);

        updated.IsSuccess.Should().BeTrue();
        product.Name.Should().Be("Fresh Milk 1L");
        product.CategoryId.Should().Be(dairy);
        product.DefaultPurchaseCost.Should().Be(62.5m);
        product.TracksExpiry.Should().BeTrue();
        product.ShelfLifeDays.Should().Be(30);
    }

    [Fact]
    public void UpdateDetails_RefusesAnInvalidProduct_AndChangesNothing()
    {
        Product product = NewProduct();

        product.UpdateDetails(" ", null, product.CategoryId, null, null, null, false, 0m, null)
            .Errors.Should().ContainSingle(e => e.Code == "product.name_required");
        product.UpdateDetails("Name", null, CategoryId.Empty, null, null, null, false, 0m, null)
            .Errors.Should().ContainSingle(e => e.Code == "product.category_required");
        product.UpdateDetails("Name", null, product.CategoryId, null, null, null, false, -1m, null)
            .Errors.Should().ContainSingle(e => e.Code == "product.cost_negative");

        product.Name.Should().Be("Curated product");
    }

    [Fact]
    public void Deactivate_ThenActivate_TracksTheDiscontinuationDate()
    {
        Product product = NewProduct();
        DateOnly today = new(2026, 9, 14);

        product.Deactivate(today).IsSuccess.Should().BeTrue();
        product.IsActive.Should().BeFalse();
        product.DiscontinuedOn.Should().Be(today);
        product.Deactivate(today).Errors.Should().ContainSingle(e => e.Code == "catalog.product_already_inactive");

        product.Activate().IsSuccess.Should().BeTrue();
        product.IsActive.Should().BeTrue();
        product.DiscontinuedOn.Should().BeNull();
        product.Activate().Errors.Should().ContainSingle(e => e.Code == "catalog.product_already_active");
    }

    [Fact]
    public void FirstBarcode_BecomesPrimary_EvenWhenNotAsked()
    {
        Product product = NewProduct();

        product.AddBarcode("CODE-A", product.BaseUnitOfMeasureId, 1m, isPrimary: false, Manager, requireChecksum: false)
            .IsSuccess.Should().BeTrue();

        product.Barcodes.Should().ContainSingle().Which.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void RetiringThePrimaryBarcode_PromotesTheLongestAttachedActiveCode()
    {
        Product product = WithBarcodes("CODE-A", "CODE-B", "CODE-C");
        product.SetPrimaryBarcode("CODE-C").IsSuccess.Should().BeTrue();

        Result retired = product.RetireBarcode("code-c", Manager, Now);

        retired.IsSuccess.Should().BeTrue();
        ProductBarcode code = product.Barcodes.Single(b => b.Value == "CODE-C");
        code.IsRetired.Should().BeTrue();
        code.IsPrimary.Should().BeFalse();
        code.RetiredByUserId.Should().Be(Manager);
        product.Barcodes.Should().ContainSingle(b => b.IsPrimary).Which.Value.Should().Be("CODE-A");
    }

    [Fact]
    public void RetiredBarcode_CannotBeRetiredAgain_MadePrimary_OrAttachedAgain()
    {
        Product product = WithBarcodes("CODE-A", "CODE-B");
        product.RetireBarcode("CODE-B", Manager, Now);

        product.RetireBarcode("CODE-B", Manager, Now)
            .Errors.Should().ContainSingle(e => e.Code == "catalog.barcode_retired");
        product.SetPrimaryBarcode("CODE-B")
            .Errors.Should().ContainSingle(e => e.Code == "catalog.barcode_retired");
        product.AddBarcode("CODE-B", product.BaseUnitOfMeasureId, 1m, false, Manager, requireChecksum: false)
            .Errors.Should().ContainSingle(e => e.Code == "catalog.barcode_retired");
    }

    [Fact]
    public void BarcodeThatIsNotAttached_IsReportedAsNotAttached()
    {
        Product product = WithBarcodes("CODE-A");

        product.RetireBarcode("CODE-Z", Manager, Now)
            .Errors.Should().ContainSingle(e => e.Code == "catalog.barcode_not_attached");
        product.SetPrimaryBarcode("CODE-Z")
            .Errors.Should().ContainSingle(e => e.Code == "catalog.barcode_not_attached");
    }

    [Fact]
    public void RetiringTheOnlyBarcode_LeavesNoPrimary()
    {
        Product product = WithBarcodes("CODE-A");

        product.RetireBarcode("CODE-A", Manager, Now).IsSuccess.Should().BeTrue();

        product.Barcodes.Should().ContainSingle().Which.IsPrimary.Should().BeFalse();
    }

    [Fact]
    public void LocationSetting_IsUpsertedInPlace_AndInvalidThresholdsChangeNothing()
    {
        Product product = NewProduct();
        LocationId store = LocationId.New();

        product.SetLocationSetting(store, true, 5m, 10m, 20m, 30m, 12m).IsSuccess.Should().BeTrue();
        ProductLocationSettingId id = product.LocationSettings.Single().Id;

        product.SetLocationSetting(store, false, 0m, 4m, 8m, 16m, 6m).IsSuccess.Should().BeTrue();
        product.SetLocationSetting(store, true, 9m, 1m, 8m, 16m, 6m)
            .Errors.Should().ContainSingle(e => e.Code == "product_location.thresholds_unordered");
        product.SetLocationSetting(store, true, -1m, 1m, 8m, 16m, 6m)
            .Errors.Should().ContainSingle(e => e.Code == "product_location.quantity_negative");

        ProductLocationSetting setting = product.LocationSettings.Should().ContainSingle().Subject;
        setting.Id.Should().Be(id);
        setting.IsStocked.Should().BeFalse();
        setting.ReorderPoint.Should().Be(4m);
        setting.PreferredReplenishmentQuantity.Should().Be(6m);
    }

    [Fact]
    public void UnitConversions_RefuseDuplicates_AndCanBeRemoved()
    {
        Product product = NewProduct();
        UnitOfMeasureId caseUnit = UnitOfMeasureId.New();

        Result<ProductUnitConversionId> added = product.AddUnitConversion(caseUnit, product.BaseUnitOfMeasureId, 24m);
        added.IsSuccess.Should().BeTrue();

        product.AddUnitConversion(caseUnit, product.BaseUnitOfMeasureId, 12m)
            .Errors.Should().ContainSingle(e => e.Code == "catalog.conversion_exists");

        product.RemoveUnitConversion(added.Value).IsSuccess.Should().BeTrue();
        product.UnitConversions.Should().BeEmpty();
        product.RemoveUnitConversion(added.Value)
            .Errors.Should().ContainSingle(e => e.Code == "catalog.conversion_unknown");
    }

    [Fact]
    public void SupplierLinks_KeepOnePreferredSupplier_AndKeepTheRecordedCost()
    {
        Product product = NewProduct();
        SupplierId first = SupplierId.New();
        SupplierId second = SupplierId.New();

        product.LinkSupplier(first, "S1-001", 3, 10m, isPreferred: true).IsSuccess.Should().BeTrue();
        product.RecordSupplierReceipt(first, 41.5m, receivedQuantity: 10m);
        product.LinkSupplier(second, null, 5, null, isPreferred: true).IsSuccess.Should().BeTrue();
        product.LinkSupplier(first, "S1-002", 4, 12m, isPreferred: false).IsSuccess.Should().BeTrue();

        product.Suppliers.Should().HaveCount(2);
        product.Suppliers.Should().ContainSingle(s => s.IsPreferred).Which.SupplierId.Should().Be(second);

        ProductSupplier firstLink = product.Suppliers.Single(s => s.SupplierId == first);
        firstLink.SupplierSku.Should().Be("S1-002");
        firstLink.LeadTimeDays.Should().Be(4);
        firstLink.LastCost.Should().Be(41.5m);

        product.LinkSupplier(first, null, -1, null, false)
            .Errors.Should().ContainSingle(e => e.Code == "product_supplier.lead_time_negative");
    }

    [Fact]
    public void UnlinkingASupplier_RemovesOnlyThatLink()
    {
        Product product = NewProduct();
        SupplierId supplier = SupplierId.New();
        product.LinkSupplier(supplier, null, 2, null, false);

        product.UnlinkSupplier(supplier).IsSuccess.Should().BeTrue();
        product.Suppliers.Should().BeEmpty();
        product.UnlinkSupplier(supplier)
            .Errors.Should().ContainSingle(e => e.Code == "catalog.supplier_not_linked");
    }

    private static Product WithBarcodes(params string[] values)
    {
        Product product = NewProduct();

        foreach (string value in values)
        {
            product.AddBarcode(value, product.BaseUnitOfMeasureId, 1m, isPrimary: false, Manager, requireChecksum: false)
                .IsSuccess.Should().BeTrue();
        }

        return product;
    }

    [Fact]
    public void CancelScheduledPrice_RemovesTempAndReopensPredecessor()
    {
        Product product = NewProduct();
        ProductPriceId basePrice = product.SchedulePrice(
            null, 100m, Now, null, Manager, "Base", Now).Value;
        ProductPriceId tempPrice = product.SchedulePrice(
            null, 50m, Now.AddDays(1), Now.AddDays(2), Manager, "Promo", Now).Value;

        product.Prices.Should().HaveCount(3, "base closed + temp + resumption");
        product.Prices.Should().ContainSingle(p => p.Id == basePrice && p.EffectiveToUtc == Now.AddDays(1));

        Result<ProductPriceId> cancelled = product.CancelScheduledPrice(tempPrice, Now);

        cancelled.IsSuccess.Should().BeTrue();
        product.Prices.Should().ContainSingle().Which.Id.Should().Be(basePrice);
        product.Prices.Single().EffectiveToUtc.Should().BeNull("the base price is open-ended again");
    }

    [Fact]
    public void CancelScheduledPrice_RemovesOpenTempAndReopensPredecessor()
    {
        Product product = NewProduct();
        ProductPriceId basePrice = product.SchedulePrice(
            null, 100m, Now, null, Manager, "Base", Now).Value;
        ProductPriceId tempPrice = product.SchedulePrice(
            null, 50m, Now.AddDays(1), null, Manager, "Temp open", Now).Value;

        product.Prices.Should().HaveCount(2, "an open temp closes the base but creates no resumption");

        Result<ProductPriceId> cancelled = product.CancelScheduledPrice(tempPrice, Now);

        cancelled.IsSuccess.Should().BeTrue();
        product.Prices.Should().ContainSingle().Which.Id.Should().Be(basePrice);
        product.Prices.Single().EffectiveToUtc.Should().BeNull();
    }

    [Fact]
    public void CancelScheduledPrice_RemovesGapFillingPriceWithNoPredecessor()
    {
        Product product = NewProduct();
        ProductPriceId basePrice = product.SchedulePrice(
            null, 100m, Now, Now.AddDays(1), Manager, "Base", Now).Value;
        ProductPriceId gapFill = product.SchedulePrice(
            null, 75m, Now.AddDays(5), Now.AddDays(10), Manager, "Gap", Now).Value;

        product.Prices.Should().HaveCount(2);

        Result<ProductPriceId> cancelled = product.CancelScheduledPrice(gapFill, Now);

        cancelled.IsSuccess.Should().BeTrue();
        product.Prices.Should().ContainSingle().Which.Id.Should().Be(basePrice);
    }

    [Fact]
    public void CancelScheduledPrice_RefusesEffectivePrice()
    {
        Product product = NewProduct();
        ProductPriceId effective = product.SchedulePrice(
            null, 100m, Now, null, Manager, "Now", Now).Value;

        Result<ProductPriceId> cancelled = product.CancelScheduledPrice(effective, Now);

        cancelled.IsFailure.Should().BeTrue();
        cancelled.Errors.Should().ContainSingle().Which.Code
            .Should().Be("catalog.price_already_effective");
        product.Prices.Should().ContainSingle().Which.Id.Should().Be(effective);
    }

    [Fact]
    public void CancelScheduledPrice_RefusesWithDifferentAmountSuccessor()
    {
        Product product = NewProduct();
        // base[Now→Now+1,$100] then after[Now+2→Now+3,$75], then temp[Now+1→Now+2,$50].
        // Scheduling the differing successor before the temp avoids any resumption.
        ProductPriceId basePrice = product.SchedulePrice(
            null, 100m, Now, Now.AddDays(1), Manager, "Base", Now).Value;
        ProductPriceId afterPrice = product.SchedulePrice(
            null, 75m, Now.AddDays(2), Now.AddDays(3), Manager, "After promo", Now).Value;
        ProductPriceId tempPrice = product.SchedulePrice(
            null, 50m, Now.AddDays(1), Now.AddDays(2), Manager, "Promo", Now).Value;

        // predecessor = base (To == temp.From ✓); successor = after (From == temp.To ✓, amount 75 ≠ 100)
        Result<ProductPriceId> cancelled = product.CancelScheduledPrice(tempPrice, Now);

        cancelled.IsFailure.Should().BeTrue();
        cancelled.Errors.Should().ContainSingle().Which.Code
            .Should().Be("catalog.price_cancel_successor");
        product.Prices.Should().Contain(p => p.Id == basePrice);
        product.Prices.Should().Contain(p => p.Id == afterPrice);
        product.Prices.Should().Contain(p => p.Id == tempPrice);
    }

    [Fact]
    public void CancelScheduledPrice_RefusesChain()
    {
        Product product = NewProduct();
        // base[Now→open,$100]; temp[Now+1→Now+2,$50] closes base at Now+1 and resumes at Now+2;
        // resume2[Now+3→Now+4,$100] overlaps the resumption → resumption closed at Now+3, continuation at Now+4.
        product.SchedulePrice(null, 100m, Now, null, Manager, "Base", Now);
        ProductPriceId tempPrice = product.SchedulePrice(
            null, 50m, Now.AddDays(1), Now.AddDays(2), Manager, "Promo", Now).Value;
        ProductPriceId resume2 = product.SchedulePrice(
            null, 100m, Now.AddDays(3), Now.AddDays(4), Manager, "Resume 2", Now).Value;

        // Cancel temp: successor = resumption[Now+2,$100] (amount matches base) but the next row at
        // Now+3 also has the base amount → cancellation would renumber the chain → refused.
        Result<ProductPriceId> cancelled = product.CancelScheduledPrice(tempPrice, Now);

        cancelled.IsFailure.Should().BeTrue();
        cancelled.Errors.Should().ContainSingle().Which.Code
            .Should().Be("catalog.price_cancel_chain");
        product.Prices.Should().Contain(p => p.Id == tempPrice);
        product.Prices.Should().Contain(p => p.Id == resume2);
    }

    [Fact]
    public void CancelScheduledPrice_UnknownPriceId()
    {
        Product product = NewProduct();

        Result<ProductPriceId> cancelled = product.CancelScheduledPrice(ProductPriceId.New(), Now);

        cancelled.IsFailure.Should().BeTrue();
        cancelled.Errors.Should().ContainSingle().Which.Code
            .Should().Be("catalog.price_unknown");
    }

    private static Product NewProduct()
        => Product.Create("CURATE-01", "Curated product", CategoryId.New(), UnitOfMeasureId.New(), Manager).Value;
}
