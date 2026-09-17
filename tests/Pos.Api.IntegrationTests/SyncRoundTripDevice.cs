using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Common.Offline;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;

namespace Pos.Api.IntegrationTests;

public sealed partial class SyncRoundTripTests
{
    /// <summary>
    /// A register: its own encrypted store, the offline container exactly as
    /// <c>MauiProgram</c> composes it, and an HTTP transport pointed at the real
    /// server with this cashier's token.
    /// </summary>
    private sealed class Device : IAsyncDisposable
    {
        private static readonly byte[] Key =
            [.. Enumerable.Range(1, DeviceDatabaseInitializer.KeyLengthBytes).Select(i => (byte)i)];

        private ServiceProvider provider = null!;
        private HttpClient scoped = null!;

        public DeviceDatabaseInitializer Store { get; private set; } = null!;

        public SyncUploader Uploader => this.provider.GetRequiredService<SyncUploader>();

        public ChangeFeedDownloader Downloader => this.provider.GetRequiredService<ChangeFeedDownloader>();

        public static async Task<Device> StartAsync(
            string directory,
            PosApiFactory factory,
            string token,
            Seed seed)
        {
            Directory.CreateDirectory(directory);

            Device register = new()
            {
                Store = new DeviceDatabaseInitializer(
                    new DeviceDatabaseOptions(Path.Combine(directory, $"{seed.DeviceCode}.db")),
                    new FixedKey()),
            };

            await register.Store.InitializeAsync(CancellationToken.None);

            await using (PosDeviceDbContext enrolling = register.Store.CreateDbContext())
            {
                enrolling.DeviceProfiles.Add(
                    new DeviceStoreProfile(seed.DeviceId, seed.Store, seed.DeviceCode, DateTimeOffset.UtcNow));
                await enrolling.SaveChangesAsync(CancellationToken.None);
            }

            // The transport carries the base address and this register's
            // credentials, which is the split HttpSyncTransport was written for.
            // It goes through the test server's own handler, not through another
            // HttpClient: a request message can only be sent once.
            register.scoped = new HttpClient(new TokenHandler(factory.Server.CreateHandler(), token))
            {
                BaseAddress = new Uri("http://localhost/"),
            };

            DeviceSession session = new();
            session.SignIn(seed.Cashier, seed.DeviceId, seed.Store);

            ServiceCollection services = [];
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton(register.Store);
            // Real time, not a frozen clock: the server stamps its price rows
            // with its own now, and a register living in the past would find no
            // price in effect for anything.
            services.AddSingleton<ISystemClock>(new RealClock());
            services.AddSingleton<IDeviceConnectivityProbe>(new AlwaysOnline());
            services.AddSingleton<ISyncTransport>(new HttpSyncTransport(register.scoped));
            services.AddSingleton(session);

            services.AddOfflineClientApplication();
            services.AddDeviceInfrastructure();

            register.provider = services.BuildServiceProvider();
            return register;
        }

        public PosDeviceDbContext OpenStore() => Store.CreateDbContext();

        public async Task<DocumentNumber> NextNumberAsync(DocumentType type)
        {
            await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<IDocumentNumberGenerator>()
                .NextAsync(type, CancellationToken.None);
        }

        public async Task<Result<TResult>> SendAsync<TResult>(ICommand<TResult> command)
        {
            await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<IDispatcher>()
                .SendAsync(command, CancellationToken.None);
        }

        /// <summary>
        /// Puts stock on the register's own shelf, the way a goods receipt would
        /// once receiving is a device use case. It posts through the same ledger,
        /// so the sale that follows draws on a real balance.
        /// </summary>
        public async Task ReceiveStockAsync(Seed seed, decimal quantity)
        {
            await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
            IInventoryLedger ledger = scope.ServiceProvider.GetRequiredService<IInventoryLedger>();

            LocationId supplier = LocationId.New();

            Result<PostedMovementGroup> posted = await ledger.PostAsync(
                new MovementGroupSpec(
                    Pos.Domain.Common.EventId.New(),
                    InventoryMovementType.SupplierReceipt,
                    ReferenceDocumentType.GoodsReceipt,
                    Guid.CreateVersion7(),
                    "GRN-2026-000001",
                    [
                        new MovementLegSpec(seed.Product, null, supplier, LocationKind.External,
                            InventoryState.External, -quantity, 18m, false),
                        new MovementLegSpec(seed.Product, null, seed.Store, LocationKind.Store,
                            InventoryState.Available, +quantity, 18m, false),
                    ],
                    new LedgerActor(seed.Cashier, seed.Cashier, seed.DeviceId, CorrelationId.New()),
                    DateTimeOffset.UtcNow,
                    DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime)),
                CancellationToken.None);

            posted.IsSuccess.Should().BeTrue(posted.IsFailure ? posted.Error.ToString() : string.Empty);

            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        /// <summary>Rings one product up for cash, as a cashier would.</summary>
        public async Task<Result<SaleId>> SellAsync(Seed seed, CashierShiftId shiftId, decimal quantity)
        {
            DocumentNumber number = await NextNumberAsync(DocumentType.Sale);

            return await SendAsync(new CompleteSaleCommand(
                number,
                Pos.Domain.Common.EventId.New(),
                seed.Store,
                shiftId,
                seed.DeviceId,
                seed.Cashier,
                null,
                DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime),
                DateTimeOffset.UtcNow,
                [new CompleteSaleLine(seed.Product, quantity, seed.Unit, null, null, null, 0m, null, false, null)],
                [new CompleteSalePayment(PaymentMethod.Cash, 45m * quantity, 45m * quantity, null)]));
        }

        public async Task<string> SaleNumberAsync(SaleId saleId)
        {
            await using PosDeviceDbContext context = OpenStore();
            return await context.LocalSales
                .AsNoTracking()
                .Where(s => s.Id == saleId)
                .Select(s => s.Number)
                .SingleAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await this.provider.DisposeAsync();
            this.scoped.Dispose();
        }

        /// <summary>Attaches this register's credentials to everything it sends.</summary>
        private sealed class TokenHandler : DelegatingHandler
        {
            private readonly string _token;

            public TokenHandler(HttpMessageHandler inner, string token)
                : base(inner)
                => this._token = token;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this._token);
                return base.SendAsync(request, cancellationToken);
            }
        }

        private sealed class FixedKey : IDeviceDatabaseKeyProvider
        {
            public ValueTask<byte[]> GetDatabaseKeyAsync(CancellationToken cancellationToken)
                => ValueTask.FromResult(Key);
        }

        private sealed class RealClock : ISystemClock
        {
            public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

            public DateOnly BusinessDateFor(string timeZoneId)
                => DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        }

        private sealed class AlwaysOnline : IDeviceConnectivityProbe
        {
            public Pos.Shared.Devices.DeviceConnectivityState Current
                => Pos.Shared.Devices.DeviceConnectivityState.Online;
        }
    }
}
