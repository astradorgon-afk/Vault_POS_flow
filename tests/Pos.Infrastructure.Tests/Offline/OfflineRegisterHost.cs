using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Common.Offline;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// A register as <c>MauiProgram</c> composes one, with a store baseline that
/// lists two cashiers, so offline sign-in, trading and upload can be exercised
/// against the real encrypted store and the real device container.
/// </summary>
internal sealed class OfflineRegisterHost : IAsyncDisposable
{
    /// <summary>A cheap work factor: the tests exercise the logic, not the cost.</summary>
    public const int TestIterations = 1_000;

    public const string Password = "cash1234";

    private ServiceProvider provider = null!;

    public TemporaryDeviceDatabase Database { get; private set; } = null!;

    public DeviceId DeviceId { get; private set; }

    public LocationId LocationId { get; private set; }

    public UserId CashierId { get; private set; }

    public UserId OtherCashierId { get; private set; }

    public DeviceSession Session { get; } = new();

    public DeviceOfflineSignIn SignIn { get; private set; } = null!;

    public DeviceOfflineSales Sales { get; private set; } = null!;

    public DeviceShiftMirror Shifts { get; private set; } = null!;

    public DeviceOutboxUploader Uploader { get; private set; } = null!;

    public static async Task<OfflineRegisterHost> StartAsync(string[]? cashierPermissions = null)
    {
        OfflineRegisterHost host = new()
        {
            Database = await TemporaryDeviceDatabase.CreateAsync(),
            LocationId = LocationId.New(),
            CashierId = UserId.New(),
            OtherCashierId = UserId.New(),
        };

        host.DeviceId = await host.Database.EnrolAsync("D03", host.LocationId);
        await host.LoadBaselineAsync(cashierPermissions ?? [Permissions.Sales.Create, Permissions.Sales.OpenShift]);

        host.SignIn = new DeviceOfflineSignIn(host.Database.Initializer, host.Database.Clock, TestIterations);
        host.Shifts = new DeviceShiftMirror(host.Database.Initializer, host.Database.Clock);
        host.Uploader = new DeviceOutboxUploader(host.Database.Initializer, host.Database.Clock);

        ServiceCollection services = [];
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddOfflineClientApplication();
        services.AddSingleton(host.Database.Initializer);
        services.AddSingleton<ISystemClock>(host.Database.Clock);
        services.AddSingleton(host.Session);
        services.AddSingleton<IDeviceProfileAccessor>(host.Database.Profiles);
        services.AddSingleton<ICurrentUser, DeviceCurrentUser>();
        services.AddSingleton<IPermissionEvaluator, DeviceSnapshotPermissionEvaluator>();
        services.AddSingleton<INegativeStockAttemptRecorder, DeviceNegativeStockAttemptRecorder>();
        services.AddScoped(sp => sp.GetRequiredService<DeviceDatabaseInitializer>().CreateDbContext());
        services.AddScoped<IUnitOfWork, DeviceUnitOfWork>();
        services.AddScoped<IAuditWriter, DeviceAuditWriter>();
        services.AddScoped<IDeviceOutbox, DeviceOutbox>();
        services.AddScoped<IShiftRepository, DeviceShiftRepository>();
        services.AddScoped<IDocumentNumberGenerator, DeviceDocumentNumberGenerator>();
        services.AddSingleton<DeviceOfflineSales>();
        host.provider = services.BuildServiceProvider();

        host.Sales = host.provider.GetRequiredService<DeviceOfflineSales>();
        return host;
    }

    /// <summary>Writes the store data a connected sign-in downloads: the store and both cashiers.</summary>
    public async Task LoadBaselineAsync(
        string[] cashierPermissions,
        bool includeCashier = true,
        bool cashierActive = true,
        TimeSpan? snapshotLifetime = null)
    {
        DateTimeOffset now = Database.Clock.UtcNow;
        DateTimeOffset expires = now + (snapshotLifetime ?? TimeSpan.FromHours(72));

        List<ChangeFeedChange> changes =
        [
            new LocationChanged(
                0, LocationId, "STORE-1", "Store One", LocationKind.Store, "Asia/Manila", "PHP", true,
                (LocationSettings.Default with { CashRoundingIncrement = 0.25m }).ToJson()),
            new UserChanged(0, OtherCashierId, "cashier2", "Other Cashier", true, 0),
            new PermissionSnapshotIssued(
                0, OtherCashierId, 1, now, expires,
                [new PermissionSnapshotGrant(Permissions.Sales.Create, LocationId),
                 new PermissionSnapshotGrant(Permissions.Sales.OpenShift, LocationId)]),
        ];

        if (includeCashier)
        {
            changes.Add(new UserChanged(0, CashierId, "cashier", "Maria Santos", cashierActive, 0));
            changes.Add(new PermissionSnapshotIssued(
                0, CashierId, 1, now, expires,
                [.. cashierPermissions.Select(p => new PermissionSnapshotGrant(p, LocationId))]));
        }

        Result<ChangeFeedApplyOutcome> applied = await Database.Applier.ReplaceBaselineAsync(
            new ChangeFeedBaseline(10, changes));
        applied.IsSuccess.Should().BeTrue(applied.IsFailure ? applied.Error.ToString() : string.Empty);
    }

    /// <summary>Signs someone in at the register, as the session would after either sign-in path.</summary>
    public void SignInAs(UserId userId) => Session.SignIn(userId, DeviceId, LocationId);

    /// <summary>Opens a shift offline through the device's own use case.</summary>
    public async Task<CashierShiftId> OpenShiftAsync()
    {
        DocumentNumber number;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            number = await scope.ServiceProvider
                .GetRequiredService<IDocumentNumberGenerator>()
                .NextAsync(DocumentType.CashierShift, CancellationToken.None);
        }

        await using AsyncServiceScope send = provider.CreateAsyncScope();
        Result<CashierShiftId> opened = await send.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(new OpenShiftCommand(number, LocationId, new DateOnly(2026, 9, 17), 1000m), CancellationToken.None);

        opened.IsSuccess.Should().BeTrue(opened.IsFailure ? opened.Error.ToString() : string.Empty);
        return opened.Value;
    }

    /// <summary>A two-line cash sale paid exactly: 2 × 45.50 + 1 × 12.00 = 103.00, tendered 110.</summary>
    public static OfflineSaleRequest CashSale(
        CashierShiftId shiftId,
        decimal? amount = null,
        PaymentMethod method = PaymentMethod.Cash,
        decimal? tendered = 110m,
        string? number = null,
        Guid? eventId = null)
        => new(
            shiftId,
            new DateOnly(2026, 9, 17),
            null,
            null,
            [
                new OfflineSaleLine(Guid.CreateVersion7(), Guid.CreateVersion7(), "SKU-1", "Rice 1kg", "4800001", 2m, 45.50m),
                new OfflineSaleLine(Guid.CreateVersion7(), Guid.CreateVersion7(), "SKU-2", "Soap", null, 1m, 12m),
            ],
            [new OfflineSalePayment(method, amount ?? 103m, tendered)],
            number,
            eventId);

    public ValueTask DisposeAsync()
    {
        provider.Dispose();
        return Database.DisposeAsync();
    }
}
