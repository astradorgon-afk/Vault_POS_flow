using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Common.Offline;
using Pos.Application.Identity;
using Pos.Application.Inventory;
using Pos.Domain.Common;

namespace Pos.Architecture.Tests;

/// <summary>
/// The device command boundary. A POS device runs the same handlers as the
/// server, so the only thing standing between an offline terminal and a use
/// case that needs central authority is which handlers are registered. These
/// tests assert that boundary from both sides: the whitelist matches the
/// documented capability table, and everything outside it fails closed.
/// </summary>
public sealed class OfflineCommandBoundaryTests
{
    private static readonly Assembly Application = typeof(Pos.Application.DependencyInjection).Assembly;

    /// <summary>
    /// The capability table in OFFLINE_SYNC.md §1, as command names. This list
    /// is duplicated on purpose: a command added to the catalogue without a
    /// deliberate edit here fails the build, so nothing becomes offline-capable
    /// as a side effect of another change.
    /// </summary>
    private static readonly string[] DocumentedOfflineUseCases =
    [
        "AddQuarantinePhotoCommand",
        "CancelInventoryCountCommand",
        "CloseShiftCommand",
        "CompleteSaleCommand",
        "CreateGoodsReceiptCommand",
        "CreateQuarantineIncidentCommand",
        "CreateSalesReturnCommand",
        "CreateStockAdjustmentCommand",
        "CreateTransferCommand",
        "InitiateEmergencyTransferCommand",
        "OpenInventoryCountCommand",
        "OpenShiftCommand",
        "ReceiveTransferCommand",
        "RecordCountLinesCommand",
        "RefundSalesReturnCommand",
        "ReprintSaleReceiptCommand",
        "ResumeShiftCommand",
        "SubmitInventoryCountCommand",
        "SubmitStockAdjustmentCommand",
        "SubmitTransferCommand",
        "SuspendShiftCommand",
        "VoidSaleCommand",
    ];

    [Fact]
    public void Catalogue_DeclaresExactlyTheDocumentedUseCases()
    {
        string[] declared = [.. OfflineCommandCatalogue.Default.All
            .Select(e => e.CommandType.Name)
            .OrderBy(n => n, StringComparer.Ordinal)];

        declared.Should().Equal(
            DocumentedOfflineUseCases,
            "the offline whitelist is the executable form of the OFFLINE_SYNC.md §1 capability table");
    }

    [Fact]
    public void EveryDeclaredUseCase_RequiresOnlyOfflineCapablePermissions()
    {
        foreach (OfflineCommandDefinition entry in OfflineCommandCatalogue.Default.All)
        {
            entry.Permissions.Should().NotBeEmpty(
                "{0} is authorized by the pipeline and must say which permission it needs",
                entry.CommandType.Name);

            foreach (string code in entry.Permissions)
            {
                PermissionDefinition? permission = Permissions.Find(code);

                permission.Should().NotBeNull("{0} names permission {1}", entry.CommandType.Name, code);
                permission!.IsOfflineCapable.Should().BeTrue(
                    "a device snapshot carries no other permissions, so {0} could never be authorized offline",
                    code);
            }
        }
    }

    [Fact]
    public void EveryDeclaredUseCase_DeclaresThePermissionItActuallyChecks()
    {
        foreach (OfflineCommandDefinition entry in OfflineCommandCatalogue.Default.All)
        {
            if (!typeof(IAuthorizedMessage).IsAssignableFrom(entry.CommandType))
            {
                continue;
            }

            // The commands carry their permission on an instance property, so
            // reading it needs an instance; none of them touches its own state
            // to answer, which is why an uninitialized one suffices.
            IAuthorizedMessage message = (IAuthorizedMessage)RuntimeHelpers.GetUninitializedObject(entry.CommandType);

            entry.Permissions.Should().Contain(
                message.RequiredPermission,
                "the catalogue entry for {0} must match the permission the pipeline enforces",
                entry.CommandType.Name);
        }
    }

    [Fact]
    public void EveryDeclaredUseCase_HasAHandlerToRegister()
    {
        foreach (OfflineCommandDefinition entry in OfflineCommandCatalogue.Default.All)
        {
            bool handled = CommandHandlerContracts()
                .Any(contract => contract.GetGenericArguments()[0] == entry.CommandType);

            handled.Should().BeTrue(
                "{0} is declared offline-capable but no handler implements it",
                entry.CommandType.Name);
        }
    }

    [Fact]
    public void DeviceContainer_RegistersNoHandlerOutsideTheWhitelist()
    {
        using ServiceProvider provider = BuildDeviceContainer();

        foreach (Type contract in CommandHandlerContracts())
        {
            Type commandType = contract.GetGenericArguments()[0];

            if (OfflineCommandCatalogue.Default.IsRegistered(commandType))
            {
                continue;
            }

            provider.GetService(contract).Should().BeNull(
                "{0} is not registered on a device, so it must not resolve there",
                commandType.Name);
        }
    }

    [Fact]
    public void DeviceContainer_RegistersNoQueryHandlers()
    {
        using ServiceProvider provider = BuildDeviceContainer();

        foreach (Type contract in ClosedContracts(typeof(IQueryHandler<,>)))
        {
            provider.GetService(contract).Should().BeNull(
                "device reads come from the device database, not from server query handlers");
        }
    }

    [Fact]
    public void DeviceContainer_ComposesNothingOutsideTheApplicationLayer()
    {
        ServiceCollection services = [];
        services.AddOfflineClientApplication();

        foreach (ServiceDescriptor descriptor in services)
        {
            Type? implementation = descriptor.ImplementationType
                ?? descriptor.ImplementationInstance?.GetType();

            if (implementation is null)
            {
                continue;
            }

            implementation.Assembly.Should().BeSameAs(
                Application,
                "the offline composition root registers use cases only; adapters are the client's own job");
        }
    }

    [Fact]
    public async Task ServerOnlyCommand_FailsClosedThroughTheDispatcher()
    {
        // No ports at all: refusing a server-only use case must not depend on
        // anything a disconnected device might be missing.
        using ServiceProvider provider = BuildDeviceContainer();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<StockAdjustmentId> result = await dispatcher.SendAsync(
            new ApproveStockAdjustmentCommand(StockAdjustmentId.New()));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("application.handler_unavailable");
        result.Error.Type.Should().Be(ErrorType.Unavailable);
    }

    [Fact]
    public async Task RegisteredUseCase_ReachesThePipelineAndIsStillAuthorized()
    {
        // The catalogue is a parameter so this can exercise the registered path
        // before the device-side adapters of any real use case exist.
        OfflineCommandCatalogue catalogue = new(
        [
            new OfflineCommandDefinition(
                typeof(SubmitInventoryCountCommand),
                OfflineCommandState.Registered,
                [Permissions.Inventory.Count],
                "exercised by the boundary tests"),
        ]);

        ICurrentUser currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(UserId.New());

        IPermissionEvaluator permissions = Substitute.For<IPermissionEvaluator>();
        permissions
            .HasPermissionAsync(Arg.Any<UserId>(), Arg.Any<string>(), Arg.Any<LocationId?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        ServiceCollection services = [];
        services.AddLogging();
        services.AddOfflineClientApplication(catalogue);
        services.AddSingleton(currentUser);
        services.AddSingleton(permissions);
        services.AddSingleton(Substitute.For<IInventoryControlRepository>());
        services.AddSingleton(Substitute.For<IAuditWriter>());
        services.AddSingleton(Substitute.For<ISystemClock>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(Substitute.For<INegativeStockAttemptRecorder>());

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider
            .GetService<ICommandHandler<SubmitInventoryCountCommand, InventoryCountId>>()
            .Should().NotBeNull("a whitelisted use case is registered on the device");

        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<InventoryCountId> result = await dispatcher.SendAsync(
            new SubmitInventoryCountCommand(InventoryCountId.New()));

        // It got past the dispatcher and was stopped by authorization, not by
        // the absence of a handler: offline does not widen authority.
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("auth.permission_denied");
        result.Error.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public void Catalogue_RefusesATypeThatIsNotACommand()
    {
        Action build = () => _ = new OfflineCommandCatalogue(
        [
            new OfflineCommandDefinition(typeof(string), OfflineCommandState.Pending, ["sale.create"], "not a command"),
        ]);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Catalogue_RefusesTheSameCommandTwice()
    {
        OfflineCommandDefinition entry = new(
            typeof(SubmitInventoryCountCommand),
            OfflineCommandState.Pending,
            [Permissions.Inventory.Count],
            "declared twice");

        Action build = () => _ = new OfflineCommandCatalogue([entry, entry]);

        build.Should().Throw<ArgumentException>();
    }

    private static ServiceProvider BuildDeviceContainer()
    {
        ServiceCollection services = [];
        services.AddOfflineClientApplication();
        return services.BuildServiceProvider();
    }

    private static IEnumerable<Type> CommandHandlerContracts() => ClosedContracts(typeof(ICommandHandler<,>));

    private static IEnumerable<Type> ClosedContracts(Type openGenericInterface)
        => Application.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGenericInterface)
            .Distinct();
}
