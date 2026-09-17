using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Inventory;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>Tests <see cref="CompleteSaleCommandHandler"/>.</summary>
public sealed class CompleteSaleCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 14);
    private static readonly UserId Manager = UserId.New();

    private static readonly LocationId TestShiftLocation = LocationId.New();
    private static readonly DeviceId TestDevice = DeviceId.New();
    private static readonly UserId TestCashier = UserId.New();
    private static readonly CashierShiftId TestShiftId = CashierShiftId.New();
    private static readonly DocumentNumber TestSaleNumber =
        DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, "D01", 1);

    private readonly ISalesRepository _repository = Substitute.For<ISalesRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly IExpiryService _expiry = Substitute.For<IExpiryService>();
    private readonly IInventoryLedger _ledger = Substitute.For<IInventoryLedger>();
    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IPermissionEvaluator _permissions = Substitute.For<IPermissionEvaluator>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private readonly CashierShift _openShift;
    private readonly CompleteSaleCommandHandler _handler;

    public CompleteSaleCommandHandlerTests()
    {
        _currentUser.DeviceId.Returns(DeviceId.New());
        _currentUser.CorrelationId.Returns(new CorrelationId(Guid.NewGuid()));

        // A shift opened by the test cashier on the test device. Every command
        // built by the helpers below carries TestShiftId / TestDevice /
        // TestCashier and the test's own location, so the shift validation the
        // handler performs passes before any business rule is evaluated.
        _openShift = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", 1),
            TestShiftLocation,
            TestDevice,
            TestCashier,
            0m,
            BusinessDate,
            Now).Value;

        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns(new ShiftDeviceFacts("D01"));
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns(_openShift);

        _customers.GetByIdAsync(Arg.Any<CustomerId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Customer?>(null));

        _ledger.PostAsync(Arg.Any<MovementGroupSpec>(), Arg.Any<CancellationToken>())
            .Returns(Result<PostedMovementGroup>.Success(new PostedMovementGroup(
                MovementGroupId.New(),
                EventId.New(),
                2,
                false,
                Now)));

        _handler = new CompleteSaleCommandHandler(
            _repository,
            _customers,
            _expiry,
            _ledger,
            _shifts,
            _permissions,
            _audit,
            _currentUser);
    }

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ReturnsSuccess()
    {
        (CompleteSaleCommand command, Product product) = SetupHappyPath(sellableQuantity: 10m);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(SaleId.Empty);
    }

    [Fact]
    public async Task HappyPath_PersistsSale()
    {
        (CompleteSaleCommand command, Product product) = SetupHappyPath(sellableQuantity: 10m);
        SaleId expectedId = SaleId.New();
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(expectedId));

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.Value.Should().Be(expectedId);
        await _repository.Received(1).AddAsync(
            Arg.Any<Sale>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_PostsLedgerWithTwoLegsPerItem()
    {
        (CompleteSaleCommand command, Product product) = SetupHappyPath(sellableQuantity: 10m);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _ledger.Received(1).PostAsync(
            Arg.Is<MovementGroupSpec>(g =>
                g.MovementType == InventoryMovementType.PosSale
                && g.ReferenceDocumentType == ReferenceDocumentType.Sale
                && g.EventId == command.EventId
                && g.Legs.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_NegativeLegIsAtStoreAvailable()
    {
        LocationId locationId = LocationId.New();
        Product product = NewPricedProduct();
        LocationSettings settings = LocationSettings.Default;
        (CompleteSaleCommand command, _) = BuildCommand(
            locationId,
            new SaleLocationFacts(LocationKind.Store, settings),
            product);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _ledger.Received(1).PostAsync(
            Arg.Is<MovementGroupSpec>(g =>
                g.Legs[0].QuantityDelta < 0m
                && g.Legs[0].LocationId == locationId
                && g.Legs[0].State == InventoryState.Available),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_PositiveLegIsAtExternalCustomer()
    {
        LocationId externalId = LocationId.New();
        (CompleteSaleCommand command, _) = SetupHappyPath(sellableQuantity: 10m);
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(externalId);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _ledger.Received(1).PostAsync(
            Arg.Is<MovementGroupSpec>(g =>
                g.Legs[1].QuantityDelta > 0m
                && g.Legs[1].LocationId == externalId
                && g.Legs[1].State == InventoryState.External),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_PersistsCallerSuppliedNumber()
    {
        (CompleteSaleCommand command, _) = SetupHappyPath(sellableQuantity: 10m);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _repository.Received(1).AddAsync(
            Arg.Is<Sale>(s => s.Number == command.Number.Value),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesSaleCompletedAudit()
    {
        (CompleteSaleCommand command, _) = SetupHappyPath(sellableQuantity: 10m);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.SaleCompleted
                && e.EntityType == "sale"
                && e.ReferenceDocumentType == ReferenceDocumentType.Sale),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Location validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownLocation_ReturnsNotFound()
    {
        LocationId locationId = LocationId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns((SaleLocationFacts?)null);

        var command = CommandWithLocation(locationId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.LocationUnknown(locationId));
    }

    [Fact]
    public async Task ExternalLocation_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.External, LocationSettings.Default));

        var command = CommandWithLocation(locationId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.LocationExternal);
    }

    [Fact]
    public async Task ZeroVatRateLocation_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(
                LocationKind.Store,
                LocationSettings.Default with { VatRate = 0m }));

        var command = CommandWithLocation(locationId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.VatRateInvalid(locationId));
    }

    // ------------------------------------------------------------------
    // Shift and number validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task MissingNumber_ReturnsValidation()
    {
        LocationId locationId = LocationId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));

        var command = CommandWithLocation(locationId) with { Number = default };

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.NumberInvalid);
    }

    [Fact]
    public async Task UnknownDevice_ReturnsNotFound()
    {
        LocationId locationId = LocationId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns((ShiftDeviceFacts?)null);

        var command = CommandWithLocation(locationId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.DeviceUnknown(TestDevice));
    }

    [Fact]
    public async Task NumberFromAnotherDevice_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns(new ShiftDeviceFacts("D99"));

        var command = CommandWithLocation(locationId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.NumberDeviceMismatch);
    }

    [Fact]
    public async Task UnknownShift_ReturnsNotFound()
    {
        LocationId locationId = LocationId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);

        var command = CommandWithLocation(locationId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.ShiftUnknown(command.CashierShiftId));
    }

    [Fact]
    public async Task SuspendedShift_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        Product product = NewPricedProduct();
        ProductId productId = product.Id;
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.Empty, "", 10m, null, 10m),
            });

        _openShift.Suspend();
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns(_openShift);

        var command = CommandWithLines(locationId, productId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleCommandErrors.ShiftNotOpen(ShiftStatus.Suspended).Code);
    }

    [Fact]
    public async Task SaleFromAnotherCashier_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));

        var command = CommandWithLocation(locationId) with { CashierId = UserId.New() };

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.ShiftCashierMismatch);
    }

    [Fact]
    public async Task SaleFromAnotherDevice_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        DeviceId otherDevice = DeviceId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        // Another real device exists for the same short code, but only the
        // shift owner may post into the shift.
        _shifts.GetDeviceFactsAsync(otherDevice, Arg.Any<CancellationToken>())
            .Returns(new ShiftDeviceFacts("D01"));

        var command = CommandWithLocation(locationId) with { DeviceId = otherDevice };

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.ShiftDeviceMismatch);
    }

    // ------------------------------------------------------------------
    // Customer validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task NamedCustomer_MustExist()
    {
        (CompleteSaleCommand command, _) = SetupHappyPath(sellableQuantity: 10m);
        CustomerId customerId = CustomerId.New();
        command = command with { CustomerId = customerId };

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.Error.Should().Be(SaleCommandErrors.CustomerUnknown(customerId));
        await _ledger.DidNotReceiveWithAnyArgs().PostAsync(default!, default);
    }

    [Fact]
    public async Task NamedCustomer_MustBeActive()
    {
        (CompleteSaleCommand command, _) = SetupHappyPath(sellableQuantity: 10m);
        Customer customer = Customer.Create(CustomerId.New(), "Customer", null, null, null, null, Manager, Now).Value;
        customer.Deactivate("Closed", Manager, Now.AddMinutes(1));
        _customers.GetByIdAsync(customer.Id, Arg.Any<CancellationToken>()).Returns(customer);
        command = command with { CustomerId = customer.Id };

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.Error.Should().Be(SaleCommandErrors.CustomerInactive(customer.Id));
        await _ledger.DidNotReceiveWithAnyArgs().PostAsync(default!, default);
    }

    [Fact]
    public async Task ActiveNamedCustomer_IsStampedOnSale()
    {
        (CompleteSaleCommand command, _) = SetupHappyPath(sellableQuantity: 10m);
        Customer customer = Customer.Create(CustomerId.New(), "Customer", null, null, null, null, Manager, Now).Value;
        _customers.GetByIdAsync(customer.Id, Arg.Any<CancellationToken>()).Returns(customer);
        command = command with { CustomerId = customer.Id };

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<Sale>(sale => sale.CustomerId == customer.Id), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Product validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownProduct_ReturnsNotFound()
    {
        ProductId productId = ProductId.New();
        LocationId locationId = LocationId.New();
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>());

        var command = CommandWithLines(locationId, productId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.ProductUnknown(productId));
    }

    // ------------------------------------------------------------------
    // Price resolution
    // ------------------------------------------------------------------

    [Fact]
    public async Task NoPriceForProduct_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        Product product = NewProduct(); // No price scheduled
        ProductId productId = product.Id;

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });

        var command = CommandWithLines(locationId, productId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleCommandErrors.PriceMissing(productId, locationId).Code);
    }

    [Fact]
    public async Task PriceOverride_AllowsSaleWithoutEffectivePrice()
    {
        LocationId locationId = LocationId.New();
        Product product = NewProduct(); // No price scheduled
        ProductId productId = product.Id;
        UserId authorizer = UserId.New();

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(SaleId.New()));
        _permissions.HasPermissionAsync(
                Arg.Any<UserId>(), Permissions.Sales.PriceOverride, Arg.Any<LocationId?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.Empty, "", 10m, null, 10m),
            });

        var command = new CompleteSaleCommand(
            TestSaleNumber,
            EventId.New(),
            locationId,
            TestShiftId,
            TestDevice,
            TestCashier,
            null,
            BusinessDate,
            Now,
            [new CompleteSaleLine(productId, 1m, UnitOfMeasureId.New(), null, 85m, authorizer, 0m, null, false, null)],
            [new CompleteSalePayment(PaymentMethod.Cash, 85m, 85m, null)]);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Permissions
    // ------------------------------------------------------------------

    [Fact]
    public async Task DiscountWithoutPermission_ReturnsForbidden()
    {
        LocationId locationId = LocationId.New();
        UserId authorizer = UserId.New();
        (CompleteSaleCommand command, _) = BuildCommandWithDiscount(
            locationId, discount: 10m, authorizer);

        _permissions.HasPermissionAsync(
                authorizer, Permissions.Sales.Discount, locationId, Arg.Any<CancellationToken>())
            .Returns(false);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.DiscountNotAuthorized);
    }

    [Fact]
    public async Task DiscountWithPermission_Succeeds()
    {
        LocationId locationId = LocationId.New();
        UserId authorizer = UserId.New();
        (CompleteSaleCommand command, _) = BuildCommandWithDiscount(
            locationId, discount: 10m, authorizer);

        _permissions.HasPermissionAsync(
                authorizer, Permissions.Sales.Discount, locationId, Arg.Any<CancellationToken>())
            .Returns(true);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task PriceOverrideWithoutPermission_ReturnsForbidden()
    {
        LocationId locationId = LocationId.New();
        UserId authorizer = UserId.New();
        (CompleteSaleCommand command, _) = BuildCommandWithPriceOverride(
            locationId, overridePrice: 80m, authorizer);

        _permissions.HasPermissionAsync(
                authorizer, Permissions.Sales.PriceOverride, locationId, Arg.Any<CancellationToken>())
            .Returns(false);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.PriceOverrideNotAuthorized);
    }

    [Fact]
    public async Task PriceOverrideWithPermission_Succeeds()
    {
        LocationId locationId = LocationId.New();
        UserId authorizer = UserId.New();
        (CompleteSaleCommand command, _) = BuildCommandWithPriceOverride(
            locationId, overridePrice: 80m, authorizer);

        _permissions.HasPermissionAsync(
                authorizer, Permissions.Sales.PriceOverride, locationId, Arg.Any<CancellationToken>())
            .Returns(true);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // FEFO allocation
    // ------------------------------------------------------------------

    [Fact]
    public async Task InsufficientStock_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        (CompleteSaleCommand command, _) = BuildCommandWithQuantity(
            locationId, quantity: 5m);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("inventory.insufficient_stock");
    }

    [Fact]
    public async Task Fefo_AllocatesEarliestExpiryFirst()
    {
        LocationId locationId = LocationId.New();
        LocationSettings settings = LocationSettings.Default;
        Product product = NewBatchTrackedPricedProduct();
        ProductId productId = product.Id;
        UnitOfMeasureId uomId = UnitOfMeasureId.New();
        BatchId earlyBatch = BatchId.New();
        BatchId lateBatch = BatchId.New();

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, settings));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(SaleId.New()));

        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, lateBatch, "LOT-LATE", 10m, new DateOnly(2026, 12, 31), 10m),
                new(productId, earlyBatch, "LOT-EARLY", 10m, new DateOnly(2026, 10, 1), 10m),
            });

        var command = new CompleteSaleCommand(
            TestSaleNumber, EventId.New(), locationId, TestShiftId, TestDevice,
            TestCashier, null, BusinessDate, Now,
            [new CompleteSaleLine(productId, 3m, uomId, null, null, null, 0m, null, false, null)],
            [new CompleteSalePayment(PaymentMethod.Cash, 300m, 300m, null)]);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Verify the first slice is from the early-expiry batch.
        await _repository.Received(1).AddAsync(
            Arg.Is<Sale>(s =>
                s.Items[0].BatchId == earlyBatch
                && s.Items[0].Quantity == 3m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultiSliceAllocation_LineIsSplitAcrossSlices()
    {
        LocationId locationId = LocationId.New();
        Product product = NewBatchTrackedPricedProduct();
        ProductId productId = product.Id;
        UnitOfMeasureId uomId = UnitOfMeasureId.New();
        BatchId batchA = BatchId.New();
        BatchId batchB = BatchId.New();

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(SaleId.New()));

        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, batchA, "LOT-A", 2m, new DateOnly(2026, 10, 1), 10m),
                new(productId, batchB, "LOT-B", 3m, new DateOnly(2026, 11, 1), 10m),
            });

        var command = new CompleteSaleCommand(
            TestSaleNumber, EventId.New(), locationId, TestShiftId, TestDevice,
            TestCashier, null, BusinessDate, Now,
            [new CompleteSaleLine(productId, 5m, uomId, null, null, null, 0m, null, false, null)],
            [new CompleteSalePayment(PaymentMethod.Cash, 500m, 500m, null)]);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<Sale>(s =>
                s.Items.Count == 2
                && s.Items[0].BatchId == batchA && s.Items[0].Quantity == 2m
                && s.Items[1].BatchId == batchB && s.Items[1].Quantity == 3m),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Expired override
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExpiredOverride_WhenSellableInsufficient_UsesExpiredBatches()
    {
        LocationId locationId = LocationId.New();
        Product product = NewBatchTrackedPricedProduct();
        ProductId productId = product.Id;
        UnitOfMeasureId uomId = UnitOfMeasureId.New();
        BatchId expiredBatch = BatchId.New();

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(SaleId.New()));

        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.New(), "SELLABLE", 1m, new DateOnly(2026, 10, 1), 10m),
            });

        _repository.GetExpiredAvailableBatchesAsync(
                locationId, productId, BusinessDate, Arg.Any<CancellationToken>())
            .Returns(new List<ExpiredSaleBatch>
            {
                new(expiredBatch, "EXPIRED", 3m, new DateOnly(2026, 9, 1), 10m),
            });

        _permissions.HasPermissionAsync(
                TestCashier, Permissions.Sales.ExpiredOverride, locationId, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new CompleteSaleCommand(
            TestSaleNumber, EventId.New(), locationId, TestShiftId, TestDevice,
            TestCashier, null, BusinessDate, Now,
            [new CompleteSaleLine(productId, 4m, uomId, null, null, null, 0m, null, true, "Customer accepted; manager directed the sale.")],
            [new CompleteSalePayment(PaymentMethod.Cash, 400m, 400m, null)]);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Verify the last item was drawn from the expired batch.
        await _repository.Received(1).AddAsync(
            Arg.Is<Sale>(s =>
                s.Items.Count == 2
                && s.Items[0].Quantity == 1m  // sellable
                && s.Items[1].BatchId == expiredBatch && s.Items[1].Quantity == 3m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpiredOverride_DeniedWithoutPermission()
    {
        LocationId locationId = LocationId.New();
        Product product = NewPricedProduct();
        ProductId productId = product.Id;

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.New(), "", 1m, null, 10m),
            });
        _repository.GetExpiredAvailableBatchesAsync(
                locationId, productId, BusinessDate, Arg.Any<CancellationToken>())
            .Returns(new List<ExpiredSaleBatch>
            {
                new(BatchId.New(), "EXP", 5m, new DateOnly(2026, 9, 1), 10m),
            });

        _permissions.HasPermissionAsync(
                TestCashier, Permissions.Sales.ExpiredOverride, locationId, Arg.Any<CancellationToken>())
            .Returns(false);

        var command = new CompleteSaleCommand(
            TestSaleNumber, EventId.New(), locationId, TestShiftId, TestDevice,
            TestCashier, null, BusinessDate, Now,
            [new CompleteSaleLine(productId, 5m, UnitOfMeasureId.New(), null, null, null, 0m, null, true, "Supplier delay; clearance approved.")],
            [new CompleteSalePayment(PaymentMethod.Cash, 500m, 500m, null)]);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        // Requesting the exception path without the authority is refused outright
        // — the cashier cannot even offer to sell expired stock.
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("sale.expired_override_denied");
        result.Error.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task ExpiredOverride_WritesExpiredOverrideAudit()
    {
        LocationId locationId = LocationId.New();
        Product product = NewBatchTrackedPricedProduct();
        ProductId productId = product.Id;

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(SaleId.New()));
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.New(), "", 1m, null, 10m),
            });
        _repository.GetExpiredAvailableBatchesAsync(
                locationId, productId, BusinessDate, Arg.Any<CancellationToken>())
            .Returns(new List<ExpiredSaleBatch>
            {
                new(BatchId.New(), "EXP", 3m, new DateOnly(2026, 9, 1), 10m),
            });
        _permissions.HasPermissionAsync(
                Arg.Any<UserId>(), Permissions.Sales.ExpiredOverride, locationId, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new CompleteSaleCommand(
            TestSaleNumber, EventId.New(), locationId, TestShiftId, TestDevice,
            TestCashier, null, BusinessDate, Now,
            [new CompleteSaleLine(productId, 4m, UnitOfMeasureId.New(), null, null, null, 0m, null, true, "Customer accepted the batch.")],
            [new CompleteSalePayment(PaymentMethod.Cash, 400m, 400m, null)]);

        await _handler.HandleAsync(command, CancellationToken.None);

        // Two audit writes: expired override, then sale.completed.
        await _audit.Received(2).WriteAsync(
            Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.ExpiredOverride
                && e.Reason == "Customer accepted the batch."
                && e.NewValueJson!.Contains("\"authorizingUserId\":")),
            Arg.Any<CancellationToken>());

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditActions.Sales.SaleCompleted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpiredOverrideNotRequested_ProbesExpiredShelfOnlyToClassifyTheRefusal()
    {
        LocationId locationId = LocationId.New();
        Product product = NewPricedProduct();
        ProductId productId = product.Id;

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.New(), "", 1m, null, 10m),
            });
        // The expired shelf is never probed for allocation — but with the
        // override path not requested, the server still probes it once on the
        // failure path to classify the refusal (expired_only vs a shortfall).
        _repository.GetExpiredAvailableBatchesAsync(
                locationId, productId, BusinessDate, Arg.Any<CancellationToken>())
            .Returns(new List<ExpiredSaleBatch>
            {
                new(BatchId.New(), "EXP", 1m, new DateOnly(2026, 9, 1), 10m),
            });

        var command = new CompleteSaleCommand(
            TestSaleNumber, EventId.New(), locationId, TestShiftId, TestDevice,
            TestCashier, null, BusinessDate, Now,
            [new CompleteSaleLine(productId, 5m, UnitOfMeasureId.New(), null, null, null, 0m, null, false, null)],
            [new CompleteSalePayment(PaymentMethod.Cash, 500m, 500m, null)]);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        // Classification probe only: the expired shelf holds 1 of the 4-unit
        // shortfall, so this remains a plain shortfall, not expired_only.
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("inventory.insufficient_stock");

        await _repository.Received(1).GetExpiredAvailableBatchesAsync(
            Arg.Any<LocationId>(), Arg.Any<ProductId>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpiredOnly_RefusedWhenShortfallCoverableByExpiredButOverrideNotRequested()
    {
        LocationId locationId = LocationId.New();
        Product product = NewPricedProduct();
        ProductId productId = product.Id;

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.New(), "", 1m, null, 10m),
            });
        _repository.GetExpiredAvailableBatchesAsync(
                locationId, productId, BusinessDate, Arg.Any<CancellationToken>())
            .Returns(new List<ExpiredSaleBatch>
            {
                new(BatchId.New(), "EXP", 3m, new DateOnly(2026, 9, 1), 10m),
            });

        var command = new CompleteSaleCommand(
            TestSaleNumber, EventId.New(), locationId, TestShiftId, TestDevice,
            TestCashier, null, BusinessDate, Now,
            [new CompleteSaleLine(productId, 4m, UnitOfMeasureId.New(), null, null, null, 0m, null, false, null)],
            [new CompleteSalePayment(PaymentMethod.Cash, 400m, 400m, null)]);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        // The 3-unit shortfall is exactly coverable by the expired shelf: the
        // refusal is classified expired_only (POS.md §5) so the terminal can
        // offer the exception path.
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("inventory.expired_only");
        result.Error.Type.Should().Be(ErrorType.Conflict);
    }

    // ------------------------------------------------------------------
    // External customer location
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExternalCustomerLocationMissing_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        Product product = NewPricedProduct();
        ProductId productId = product.Id;

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns((LocationId?)null);
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.Empty, "", 10m, null, 10m),
            });

        var command = CommandWithLines(locationId, productId);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.ExternalCustomerLocationMissing);
    }

    // ------------------------------------------------------------------
    // Ledger failure
    // ------------------------------------------------------------------

    [Fact]
    public async Task LedgerFailure_ReturnsErrors()
    {
        (CompleteSaleCommand command, _) = SetupHappyPath(sellableQuantity: 10m);
        Error ledgerError = Error.Conflict("test.ledger", "Ledger refused.");
        _ledger.PostAsync(Arg.Any<MovementGroupSpec>(), Arg.Any<CancellationToken>())
            .Returns(Result<PostedMovementGroup>.Failure(ledgerError));

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ledgerError);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private (CompleteSaleCommand Command, Product Product) SetupHappyPath(decimal sellableQuantity)
    {
        LocationId locationId = LocationId.New();
        Product product = NewPricedProduct();
        ProductId productId = product.Id;

        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(SaleId.New()));
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.Empty, "", sellableQuantity, null, 10m),
            });

        CompleteSaleCommand command = CommandWithLines(locationId, productId);
        return (command, product);
    }

    private static Product NewProduct()
    {
        return Product.Create(
            $"CODE-{Guid.NewGuid():N}"[..8],
            "Widget",
            CategoryId.New(),
            UnitOfMeasureId.New(),
            Manager).Value;
    }

    /// <summary>Creates a product with an open-ended 100m price in effect now.</summary>
    private static Product NewPricedProduct()
    {
        Product product = NewProduct();
        product.SchedulePrice(null, 100m, Now, null, Manager, "Launch", Now);
        return product;
    }

    /// <summary>
    /// Creates a batch-tracked, expiry-tracked product with an open-ended
    /// 100m price in effect now.
    /// </summary>
    private static Product NewBatchTrackedPricedProduct()
    {
        Product product = Product.Create(
            $"CODE-{Guid.NewGuid():N}"[..8],
            "Widget",
            CategoryId.New(),
            UnitOfMeasureId.New(),
            Manager,
            tracksBatches: true,
            tracksExpiry: true,
            shelfLifeDays: 30).Value;
        product.SchedulePrice(null, 100m, Now, null, Manager, "Launch", Now);
        return product;
    }

    private static CompleteSaleCommand CommandWithLocation(LocationId locationId) =>
        new(
            TestSaleNumber,
            EventId.New(),
            locationId,
            TestShiftId,
            TestDevice,
            TestCashier,
            null,
            BusinessDate,
            Now,
            [new CompleteSaleLine(ProductId.New(), 1m, UnitOfMeasureId.New(), null, null, null, 0m, null, false, null)],
            [new CompleteSalePayment(PaymentMethod.Cash, 100m, 100m, null)]);

    private static CompleteSaleCommand CommandWithLines(LocationId locationId, ProductId productId) =>
        new(
            TestSaleNumber,
            EventId.New(),
            locationId,
            TestShiftId,
            TestDevice,
            TestCashier,
            null,
            BusinessDate,
            Now,
            [new CompleteSaleLine(productId, 1m, UnitOfMeasureId.New(), null, null, null, 0m, null, false, null)],
            [new CompleteSalePayment(PaymentMethod.Cash, 100m, 100m, null)]);

    private (CompleteSaleCommand Command, Product Product) BuildCommand(
        LocationId locationId,
        SaleLocationFacts locationFacts,
        Product product)
    {
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(locationFacts);
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(SaleId.New()));
        _expiry.GetSellableBatchesAsync(locationId, product.Id, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(product.Id, BatchId.Empty, "", 10m, null, 10m),
            });

        CompleteSaleCommand command = CommandWithLines(locationId, product.Id);
        return (command, product);
    }

    private (CompleteSaleCommand Command, Product Product) BuildCommandWithQuantity(
        LocationId locationId,
        decimal quantity)
    {
        Product product = NewPricedProduct();
        ProductId productId = product.Id;
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.Empty, "", 2m, null, 10m),
            });

        CompleteSaleCommand command = CommandWithLines(locationId, productId) with
        {
            Lines = [new CompleteSaleLine(productId, quantity, UnitOfMeasureId.New(), null, null, null, 0m, null, false, null)],
        };
        return (command, product);
    }

    private (CompleteSaleCommand Command, Product Product) BuildCommandWithDiscount(
        LocationId locationId,
        decimal discount,
        UserId authorizer)
    {
        Product product = NewPricedProduct();
        ProductId productId = product.Id;
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(SaleId.New()));
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.Empty, "", 10m, null, 10m),
            });

        decimal paymentAmount = 100m - discount;
        CompleteSaleCommand command = CommandWithLines(locationId, productId) with
        {
            Lines = [new CompleteSaleLine(productId, 1m, UnitOfMeasureId.New(), null, null, null, discount, authorizer, false, null)],
            Payments = [new CompleteSalePayment(PaymentMethod.Cash, paymentAmount, paymentAmount, null)],
        };
        return (command, product);
    }

    private (CompleteSaleCommand Command, Product Product) BuildCommandWithPriceOverride(
        LocationId locationId,
        decimal overridePrice,
        UserId authorizer)
    {
        Product product = NewPricedProduct();
        ProductId productId = product.Id;
        _repository.GetLocationAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        ProductPrice? effective = product.PriceAt(locationId, Now);
        _repository.GetSaleProductsAsync(
                Arg.Any<IReadOnlyCollection<ProductId>>(),
                Arg.Any<LocationId>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SaleProduct>
            {
                new(product.Id, product.Name, product.IsVatExempt, product.TracksBatches,
                    effective?.Id, effective?.Price.Amount),
            });
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(SaleId.New()));
        _expiry.GetSellableBatchesAsync(locationId, productId, Arg.Any<CancellationToken>())
            .Returns(new List<SellableBatchItem>
            {
                new(productId, BatchId.Empty, "", 10m, null, 10m),
            });

        CompleteSaleCommand command = CommandWithLines(locationId, productId) with
        {
            Lines = [new CompleteSaleLine(productId, 1m, UnitOfMeasureId.New(), null, overridePrice, authorizer, 0m, null, false, null)],
            Payments = [new CompleteSalePayment(PaymentMethod.Cash, overridePrice, overridePrice, null)],
        };
        return (command, product);
    }
}
