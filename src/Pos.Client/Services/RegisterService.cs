using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Sales;
using Pos.Client.Storage;
using Pos.Domain.Common;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Sync;

namespace Pos.Client.Services;

/// <summary>Who is signed in at this register.</summary>
/// <param name="UserId">The user.</param>
/// <param name="UserName">The username they signed in with.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="Permissions">
/// What the till may offer them: head office's answer when signed in online, the
/// cached offline snapshot when not. Every action is still checked where it runs.
/// </param>
/// <param name="Session">The head-office session, or null when they signed in offline.</param>
/// <param name="OfflineAuthorityExpiresAtUtc">When their cached offline authority runs out, for an offline sign-in.</param>
public sealed record RegisterUser(
    UserId UserId,
    string UserName,
    string DisplayName,
    IReadOnlyList<string> Permissions,
    HeadOfficeSession? Session,
    DateTimeOffset? OfflineAuthorityExpiresAtUtc = null)
{
    /// <summary>Gets a value indicating whether they signed in without head office.</summary>
    public bool IsOffline => Session is null;

    /// <summary>Gets whether they hold a permission.</summary>
    /// <param name="permission">The permission code.</param>
    /// <returns>True when the till may offer it.</returns>
    public bool Can(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>A manager's head-office session, held only while a register is being set up.</summary>
/// <param name="Server">The head-office address.</param>
/// <param name="Session">The manager's session.</param>
/// <param name="Stores">The stores a register can be set up for.</param>
public sealed record ManagerSetupSession(Uri Server, HeadOfficeSession Session, IReadOnlyList<StoreChoice> Stores);

/// <summary>One product as the register would ring it up.</summary>
/// <param name="ProductId">The product identifier sent on a sale line.</param>
/// <param name="BaseUnitOfMeasureId">The inventory unit sent on a sale line, when head office supplied it.</param>
/// <param name="Sku">The SKU.</param>
/// <param name="Name">The product name.</param>
/// <param name="Barcode">The primary barcode, if any.</param>
/// <param name="Price">The price in force at this store now, if any.</param>
/// <param name="Currency">The price's currency.</param>
public sealed record CatalogueItem(
    Guid ProductId,
    Guid? BaseUnitOfMeasureId,
    string Sku,
    string Name,
    string? Barcode,
    decimal? Price,
    string? Currency);

/// <summary>
/// Sets the register up and signs people in at it: enrolment writes the local
/// device profile, and each connected sign-in downloads the store's data and its
/// staff's offline authority into the encrypted store.
/// </summary>
/// <remarks>
/// <para>
/// When head office cannot be reached the register keeps trading on its own:
/// it signs people in against the password head office last accepted for them
/// here, opens shifts and takes cash sales from its own store data, and queues
/// all of it for head office (OFFLINE_SYNC.md §1). Only a failure to reach head
/// office is worked through this way. A refusal — a wrong password, a disabled
/// account, not enough stock — is head office's answer and is shown as it is.
/// </para>
/// <para>
/// Whatever the register queued goes up the next time the person who queued it
/// is signed in while connected: at sign-in, when the till reloads its shift,
/// and before a shift is closed.
/// </para>
/// </remarks>
/// <param name="headOffice">The head-office client.</param>
/// <param name="keys">The register's key pair.</param>
/// <param name="database">The encrypted device store.</param>
/// <param name="applier">The only writer of downloaded store data.</param>
/// <param name="session">The register's session.</param>
/// <param name="clock">The device clock.</param>
/// <param name="preferences">Where the head-office address is remembered.</param>
/// <param name="scopes">Creates short-lived scopes for the device's own use cases.</param>
/// <param name="offlineSignIn">Checks passwords while head office is unreachable.</param>
/// <param name="offlineSales">Completes cash sales while head office is unreachable.</param>
/// <param name="shifts">Keeps the register's view of its open shift.</param>
/// <param name="uploader">Delivers queued events to head office.</param>
public sealed class RegisterService(
    HeadOfficeClient headOffice,
    DeviceKeyStore keys,
    DeviceDatabaseInitializer database,
    ChangeFeedApplier applier,
    DeviceSession session,
    ISystemClock clock,
    IPreferences preferences,
    IServiceScopeFactory scopes,
    DeviceOfflineSignIn offlineSignIn,
    DeviceOfflineSales offlineSales,
    DeviceShiftMirror shifts,
    DeviceOutboxUploader uploader) : IDisposable
{
    private const string ServerPreference = "vaultflow.headoffice.address";

    // A refresh token is single-use and a second presentation is treated as
    // theft, so only one refresh may be in flight.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private IReadOnlyDictionary<Guid, ProductSaleReference> _productReferences =
        new Dictionary<Guid, ProductSaleReference>();

    // When head office last failed to answer. For a short while after, a sale or
    // a shift goes straight to the register instead of waiting on a connection
    // that just failed; the till's own check keeps asking in the background.
    private DateTimeOffset? _unreachableAt;

    /// <summary>How long a failure to reach head office sends work straight to the register.</summary>
    public static readonly TimeSpan UnreachableGrace = TimeSpan.FromSeconds(30);

    /// <summary>The address a development register points at until a manager changes it.</summary>
    public static readonly Uri DefaultServer = new("http://localhost:5177/");

    /// <summary>What a cashier is told about a register working without head office.</summary>
    public const string OfflineSaleMessaging =
        "Head office can’t be reached. This register keeps selling for cash and sends the sales " +
        "to head office when the connection is back. Card and e-wallet payments need a connection.";

    /// <summary>What a cashier is told when the checkout context cannot be loaded.</summary>
    public const string ContextUnavailableMessaging =
        "This register has no store data yet. Connect to head office and sign in once to download it.";

    /// <summary>What someone signed in offline is told about work that needs head office.</summary>
    public const string NeedsConnectionMessaging =
        "This needs head office, and you’re signed in offline. Once the connection is back, " +
        "choose Reconnect and enter your password.";

    /// <summary>What someone signed in offline is told when they try to close the shift.</summary>
    public const string CloseShiftNeedsConnectionMessaging =
        "Closing a shift needs head office, which counts the drawer against every sale, including the ones " +
        "taken offline. Keep selling; once the connection is back, choose Reconnect and then close the shift.";

    /// <summary>Gets the head-office address this register uses.</summary>
    public Uri Server =>
        Uri.TryCreate(preferences.Get(ServerPreference, string.Empty), UriKind.Absolute, out Uri? saved)
            ? saved
            : DefaultServer;

    /// <summary>Gets who is signed in, or null when the register is locked.</summary>
    public RegisterUser? Current { get; private set; }

    /// <summary>Gets what the last upload left waiting, or null before the first one.</summary>
    public DeviceUploadResult? LastUpload { get; private set; }

    /// <summary>Gets why head office refused the last upload, when it did.</summary>
    public string? LastUploadRefusal { get; private set; }

    /// <inheritdoc />
    public void Dispose() => _refreshGate.Dispose();

    /// <summary>Reads a head-office address typed by a manager.</summary>
    /// <param name="text">What was typed.</param>
    /// <returns>The address, with a trailing slash so relative paths append to it.</returns>
    public static Uri ParseServer(string? text)
    {
        string value = (text ?? string.Empty).Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new HeadOfficeException("Enter the head-office address, for example http://localhost:5177.");
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    /// <summary>Signs a manager in to issue an enrolment code from this register.</summary>
    public async Task<ManagerSetupSession> OpenManagerSessionAsync(
        Uri server, string userName, string password, CancellationToken cancellationToken = default)
    {
        HeadOfficeSession manager = await headOffice
            .SignInAsync(server, userName.Trim(), password, deviceId: null, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<StoreChoice> stores = await headOffice
            .GetStoresAsync(server, manager.AccessToken, cancellationToken)
            .ConfigureAwait(false);

        return new ManagerSetupSession(server, manager, stores);
    }

    /// <summary>Registers this machine at a store and returns its one-time enrolment code.</summary>
    /// <remarks>The manager's session is ended afterwards whatever happens; it is not needed again.</remarks>
    public async Task<string> IssueEnrolmentCodeAsync(
        ManagerSetupSession manager,
        Guid storeId,
        string registerName,
        string shortCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);

        try
        {
            return await headOffice.RegisterDeviceAsync(
                manager.Server,
                manager.Session.AccessToken,
                shortCode.Trim().ToUpperInvariant(),
                registerName.Trim(),
                storeId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await headOffice
                .SignOutAsync(manager.Server, manager.Session.RefreshToken, null, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Enrols this register with a one-time code and records who it is.</summary>
    public async Task EnrolAsync(Uri server, string enrolmentCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        string thumbprint = await keys.GetThumbprintAsync().ConfigureAwait(false);
        EnrolledDevice enrolled = await headOffice
            .EnrolAsync(server, enrolmentCode.Trim(), thumbprint, cancellationToken)
            .ConfigureAwait(false);

        await using (PosDeviceDbContext context = await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            context.DeviceProfiles.Add(new DeviceStoreProfile(
                new DeviceId(enrolled.DeviceId),
                new LocationId(enrolled.LocationId),
                enrolled.ShortCode,
                clock.UtcNow));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        preferences.Set(ServerPreference, server.AbsoluteUri);
    }

    /// <summary>
    /// Signs someone in at this register. With head office, the store's data and
    /// its staff's offline authority are downloaded first and the password is
    /// remembered for offline use; without it, the password is checked against
    /// what this register remembers.
    /// </summary>
    /// <exception cref="HeadOfficeException">Head office refused, or the register cannot sign them in offline.</exception>
    public async Task SignInAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        DeviceStoreProfile profile = await ReadProfileAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new HeadOfficeException("This register is not enrolled yet.");

        try
        {
            await SignInOnlineAsync(profile, userName, password, cancellationToken).ConfigureAwait(false);
        }
        catch (HeadOfficeUnreachableException unreachable)
        {
            Result<OfflineSignInGrant> offline = await offlineSignIn
                .SignInAsync(userName, password, cancellationToken)
                .ConfigureAwait(false);
            if (offline.IsFailure)
            {
                throw new HeadOfficeException(offline.Error.Message, unreachable);
            }

            OfflineSignInGrant grant = offline.Value;
            _productReferences = new Dictionary<Guid, ProductSaleReference>();
            session.SignIn(grant.UserId, profile.DeviceId, profile.LocationId);
            Current = new RegisterUser(
                grant.UserId,
                grant.UserName,
                grant.DisplayName,
                grant.Permissions,
                Session: null,
                grant.AuthorityExpiresAtUtc);
        }

        LastUpload = await uploader.GetBacklogAsync(Current?.UserId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns an offline sign-in into a connected one once head office is back,
    /// then sends what the register queued in the meantime.
    /// </summary>
    /// <param name="password">The signed-in person's password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="HeadOfficeException">Head office is still unreachable, or refused.</exception>
    public async Task ReconnectAsync(string password, CancellationToken cancellationToken = default)
    {
        RegisterUser user = Current ?? throw new HeadOfficeException("Sign in before using the till.");
        if (!user.IsOffline)
        {
            await UploadPendingAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        DeviceStoreProfile profile = await ReadProfileAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new HeadOfficeException("This register is not enrolled yet.");

        try
        {
            await SignInOnlineAsync(profile, user.UserName, password, cancellationToken, expected: user.UserId)
                .ConfigureAwait(false);
        }
        catch (HeadOfficeUnreachableException ex)
        {
            throw new HeadOfficeException("Head office still can’t be reached. Keep selling; try again later.", ex);
        }
    }

    /// <summary>Downloads the store's data again with the current session.</summary>
    public async Task RefreshStoreDataAsync(CancellationToken cancellationToken = default)
    {
        RegisterUser user = Current ?? throw new HeadOfficeException("Sign in to download store data.");
        if (user.IsOffline)
        {
            throw new HeadOfficeException(NeedsConnectionMessaging);
        }

        DeviceStoreProfile profile = await ReadProfileAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new HeadOfficeException("This register is not enrolled yet.");

        await WithSessionAsync(async token =>
            {
                await DownloadStoreDataAsync(profile, token, cancellationToken).ConfigureAwait(false);
                return true;
            })
            .ConfigureAwait(false);
        _productReferences = await WithSessionAsync(token => headOffice.GetProductSaleReferencesAsync(
                Server, token, profile.DeviceId.Value, cancellationToken))
            .ConfigureAwait(false);
        await UploadPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Locks the register. Anything waiting to be sent stays on the device.</summary>
    public async Task SignOutAsync()
    {
        RegisterUser? user = Current;
        Current = null;
        LastUpload = null;
        LastUploadRefusal = null;
        _productReferences = new Dictionary<Guid, ProductSaleReference>();
        DeviceId? device = session.DeviceId;
        session.SignOut();

        if (user?.Session is { } signedIn)
        {
            await headOffice.SignOutAsync(Server, signedIn.RefreshToken, device?.Value, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends what this register queued for the signed-in person. Head office
    /// being unreachable is not an error here: the work simply stays queued.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was delivered and what is still waiting.</returns>
    public async Task<DeviceUploadResult> UploadPendingAsync(CancellationToken cancellationToken = default)
    {
        RegisterUser? user = Current;
        if (user is null || user.IsOffline || session.DeviceId is not { } device)
        {
            return LastUpload = await uploader.GetBacklogAsync(user?.UserId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            LastUpload = await uploader
                .UploadAsync(
                    user.UserId,
                    (batch, token) => WithSessionAsync(access =>
                        headOffice.PushEventsAsync(Server, access, device.Value, batch, token)),
                    cancellationToken)
                .ConfigureAwait(false);
            LastUploadRefusal = null;
        }
        catch (HeadOfficeException ex)
        {
            // Unreachable, or refused outright (a suspended register, say). The
            // batch went back in the queue either way; nothing is lost, and
            // nothing a cashier is doing should fail because of it.
            LastUploadRefusal = ex is HeadOfficeUnreachableException ? null : ex.Message;
            LastUpload = await uploader.GetBacklogAsync(user.UserId, cancellationToken).ConfigureAwait(false);
        }

        return LastUpload;
    }

    /// <summary>Reads the products this register can sell, with the price in force here now.</summary>
    public async Task<IReadOnlyList<CatalogueItem>> GetCatalogueAsync(CancellationToken cancellationToken = default)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeviceStoreProfile? profile = await context.DeviceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        List<DeviceCachedProduct> products = await context.Products.AsNoTracking()
            .Where(p => p.IsActive).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<DeviceCachedProductBarcode> barcodes = await context.ProductBarcodes.AsNoTracking()
            .Where(b => b.IsActive).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<DeviceCachedProductPrice> prices = await context.ProductPrices.AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;
        LocationId? store = profile?.LocationId;

        return [.. products
            .OrderBy(p => p.Name, StringComparer.CurrentCulture)
            .Select(p =>
            {
                // A store's own price beats the business-wide one; within each,
                // the most recently effective price is the one in force.
                DeviceCachedProductPrice? price = prices
                    .Where(x => x.ProductId == p.Id
                                && (x.LocationId is null || x.LocationId == store)
                                && x.EffectiveFromUtc <= now
                                && (x.EffectiveToUtc is null || x.EffectiveToUtc > now))
                    .OrderBy(x => x.LocationId is null ? 1 : 0)
                    .ThenByDescending(x => x.EffectiveFromUtc)
                    .FirstOrDefault();
                string? barcode = barcodes
                    .Where(b => b.ProductId == p.Id)
                    .OrderBy(b => b.IsPrimary ? 0 : 1)
                    .Select(b => b.Barcode)
                    .FirstOrDefault();

                // The unit comes with the store data now, so the till can ring a
                // product up offline; head office's live answer still wins online.
                _productReferences.TryGetValue(p.Id.Value, out ProductSaleReference? reference);
                return new CatalogueItem(
                    p.Id.Value,
                    reference?.BaseUnitOfMeasureId ?? p.BaseUnitOfMeasureId?.Value,
                    p.Sku,
                    p.Name,
                    barcode,
                    price?.Amount,
                    price?.Currency);
            })];
    }

    /// <summary>Reads the enrolled store's name and this register's till code.</summary>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The store display name and the till short code.</returns>
    public async Task<(string Name, string TillCode)> GetStoreIdentityAsync(CancellationToken cancellationToken = default)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeviceStoreProfile? profile = await context.DeviceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (profile is null)
        {
            return ("This store", string.Empty);
        }

        string? name = await context.Locations.AsNoTracking()
            .Where(l => l.Id == profile.LocationId)
            .Select(l => l.Name)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return (string.IsNullOrWhiteSpace(name) ? "This store" : name, profile.ShortCode);
    }

    /// <summary>
    /// Loads the current shift and checkout facts for this register: from head
    /// office when it can be reached, from the register's own store data when not.
    /// </summary>
    public async Task<RegisterCheckoutContext> GetCheckoutContextAsync(CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();

        if (!user.IsOffline && !RecentlyUnreachable)
        {
            try
            {
                // Head office's answer about the drawer only counts once it has
                // everything this register queued; send that first.
                await UploadPendingAsync(cancellationToken).ConfigureAwait(false);

                RegisterCheckoutContext online = await WithSessionAsync(token => headOffice.GetCheckoutContextAsync(
                        Server, token, deviceId.Value, locationId.Value, cancellationToken))
                    .ConfigureAwait(false);

                await shifts.ReconcileAsync(
                        online.OpenShift is { } open
                            ? new DeviceKnownShift(
                                open.ShiftId,
                                open.Number,
                                open.CashierUserId,
                                open.CashierName,
                                open.BusinessDate,
                                open.OpeningFloat,
                                clock.UtcNow)
                            : null,
                        cancellationToken)
                    .ConfigureAwait(false);

                return online;
            }
            catch (HeadOfficeUnreachableException)
            {
                // Fall through to the register's own answer.
            }
        }

        DeviceTillContext local = await shifts.GetTillContextAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new HeadOfficeException(ContextUnavailableMessaging);

        return new RegisterCheckoutContext(
            local.BusinessDate,
            local.CashRoundingIncrement,
            local.OpenShift is { } shift
                ? new RegisterOpenShift(
                    shift.ShiftId,
                    shift.Number,
                    shift.CashierUserId,
                    shift.CashierName,
                    shift.BusinessDate,
                    shift.OpeningFloat)
                : null,
            IsOffline: true);
    }

    /// <summary>
    /// Opens a shift under this device's gap-safe SHF counter: at head office
    /// when it can be reached, on the register itself when it cannot.
    /// </summary>
    public async Task<RegisterCheckoutContext> OpenShiftAsync(
        decimal openingFloat,
        DateOnly businessDate,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        DocumentNumber number = await NextNumberAsync(DocumentType.CashierShift, cancellationToken)
            .ConfigureAwait(false);

        if (!user.IsOffline && !RecentlyUnreachable)
        {
            try
            {
                await WithSessionAsync(token => headOffice.OpenShiftAsync(
                        Server, token, deviceId.Value, number.Value, locationId.Value, businessDate, openingFloat,
                        cancellationToken))
                    .ConfigureAwait(false);

                return await GetCheckoutContextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HeadOfficeUnreachableException ex) when (ex.MayHaveArrived)
            {
                // Head office may have opened it; opening a second one here would
                // be refused on sync. Reloading shows which it was.
                throw new HeadOfficeException(
                    "Head office didn’t answer while opening the shift. Reload the till before trying again.", ex);
            }
            catch (HeadOfficeUnreachableException)
            {
                // Never reached head office: open it on the register instead.
            }
        }

        Result<CashierShiftId> opened;
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            opened = await scope.ServiceProvider
                .GetRequiredService<Pos.Application.Common.Messaging.IDispatcher>()
                .SendAsync(new OpenShiftCommand(number, locationId, businessDate, openingFloat), cancellationToken)
                .ConfigureAwait(false);
        }

        if (opened.IsFailure)
        {
            throw new HeadOfficeException(DescribeOfflineRefusal(opened.Error));
        }

        return await GetCheckoutContextAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes a fully paid cart: through head office's sale, ledger and audit
    /// transaction when it can be reached, and as a queued cash sale on the
    /// register when it cannot.
    /// </summary>
    public async Task<CompletedRegisterSale> CompleteSaleAsync(
        Guid shiftId,
        DateOnly businessDate,
        Guid? customerId,
        IReadOnlyList<RegisterSaleLine> lines,
        IReadOnlyList<RegisterSalePayment> payments,
        string? customerName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(payments);

        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();

        string? number = null;
        Guid? eventId = null;

        if (!user.IsOffline && !RecentlyUnreachable)
        {
            DocumentNumber allocated = await NextNumberAsync(DocumentType.Sale, cancellationToken).ConfigureAwait(false);
            Guid saleEvent = Guid.CreateVersion7();
            try
            {
                Guid saleId = await WithSessionAsync(token => headOffice.CompleteSaleAsync(
                        Server,
                        token,
                        deviceId.Value,
                        allocated.Value,
                        saleEvent,
                        locationId.Value,
                        shiftId,
                        customerId,
                        businessDate,
                        clock.UtcNow,
                        lines,
                        payments,
                        cancellationToken))
                    .ConfigureAwait(false);

                if (LastUpload is { Remaining: > 0 })
                {
                    // Head office answers again; send what was kept meanwhile.
                    await UploadPendingAsync(cancellationToken).ConfigureAwait(false);
                }

                return new CompletedRegisterSale(saleId, allocated.Value);
            }
            catch (HeadOfficeUnreachableException)
            {
                // Queue it under the same number and event: if head office did
                // commit it, the upload is recognised as the same sale.
                number = allocated.Value;
                eventId = saleEvent;
            }
        }

        Result<DeviceLocalSale> recorded = await offlineSales
            .RecordAsync(
                new OfflineSaleRequest(
                    new CashierShiftId(shiftId),
                    businessDate,
                    customerId,
                    customerName,
                    [.. lines.Select(line => new OfflineSaleLine(
                        line.ProductId,
                        line.UnitOfMeasureId,
                        line.Sku,
                        line.Name,
                        line.Barcode,
                        line.Quantity,
                        line.UnitPrice))],
                    [.. payments.Select(payment => new OfflineSalePayment(payment.Method, payment.Amount, payment.Tendered))],
                    number,
                    eventId),
                user.DisplayName,
                cancellationToken)
            .ConfigureAwait(false);

        if (recorded.IsFailure)
        {
            throw new HeadOfficeException(recorded.Error.Message);
        }

        LastUpload = await uploader.GetBacklogAsync(user.UserId, cancellationToken).ConfigureAwait(false);
        return new CompletedRegisterSale(recorded.Value.EventId.Value, recorded.Value.Number, IsOffline: true);
    }

    /// <summary>Gets the X-REPORT facts for the shift open on this register.</summary>
    public async Task<RegisterShiftSummary> GetShiftSummaryAsync(
        Guid shiftId,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, _) = RequireActiveSession();
        RequireConnected(user, CloseShiftNeedsConnectionMessaging);

        // The drawer is counted against head office's figures, which must
        // include everything this register sold offline.
        DeviceUploadResult backlog = await UploadPendingAsync(cancellationToken).ConfigureAwait(false);
        if (backlog.Remaining > 0)
        {
            throw new HeadOfficeException(DescribeBacklog(backlog)
                + " Close the shift once they have reached head office, so the drawer is counted against them.");
        }

        return await WithSessionAsync(token => headOffice.GetShiftSummaryAsync(
                Server, token, deviceId.Value, shiftId, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Declares and counts the drawer, then closes the shift.</summary>
    public async Task CloseShiftAsync(
        Guid shiftId,
        decimal declaredCash,
        decimal countedCash,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        RequireConnected(user, CloseShiftNeedsConnectionMessaging);

        await WithSessionAsync(token => headOffice.CloseShiftAsync(
                Server, token, deviceId.Value, shiftId, locationId.Value, declaredCash, countedCash, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Finds completed sales at this register's store.</summary>
    public Task<IReadOnlyList<RegisterSaleSummary>> SearchSalesAsync(
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        RequireConnected(user, NeedsConnectionMessaging);
        return WithSessionAsync(token => headOffice.SearchSalesAsync(
            Server, token, deviceId.Value, locationId.Value, from, to, cancellationToken));
    }

    /// <summary>Loads a completed sale's lines, ready for a return.</summary>
    public Task<RegisterSaleDetail> GetSaleDetailAsync(
        Guid saleId,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, _) = RequireActiveSession();
        RequireConnected(user, NeedsConnectionMessaging);
        return WithSessionAsync(token => headOffice.GetSaleDetailAsync(
            Server, token, deviceId.Value, saleId, cancellationToken));
    }

    /// <summary>Accepts a return against a completed sale using this device's gap-safe RET counter.</summary>
    public async Task<AcceptedRegisterReturn> AcceptReturnAsync(
        Guid saleId,
        Guid shiftId,
        DateOnly businessDate,
        Guid? customerId,
        IReadOnlyList<RegisterReturnLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        RequireConnected(user, NeedsConnectionMessaging);
        DocumentNumber number = await NextNumberAsync(DocumentType.SalesReturn, cancellationToken).ConfigureAwait(false);
        Guid returnId = await WithSessionAsync(token => headOffice.CreateReturnAsync(
                Server,
                token,
                deviceId.Value,
                number.Value,
                Guid.CreateVersion7(),
                saleId,
                locationId.Value,
                shiftId,
                customerId,
                businessDate,
                clock.UtcNow,
                lines,
                cancellationToken))
            .ConfigureAwait(false);

        return new AcceptedRegisterReturn(returnId, number.Value);
    }

    /// <summary>Issues a refund against a return through the open shift.</summary>
    public Task<Guid> RefundReturnAsync(
        Guid returnId,
        Guid? saleId,
        Guid shiftId,
        PaymentMethod method,
        decimal amount,
        decimal? tendered,
        string? providerReference,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        RequireConnected(user, NeedsConnectionMessaging);
        Guid refundEvent = Guid.CreateVersion7();
        return WithSessionAsync(token => headOffice.RefundReturnAsync(
            Server,
            token,
            deviceId.Value,
            returnId,
            saleId,
            refundEvent,
            locationId.Value,
            shiftId,
            method,
            amount,
            tendered,
            providerReference,
            clock.UtcNow,
            cancellationToken));
    }

    /// <summary>Searches customer records by name, phone or email.</summary>
    public Task<IReadOnlyList<RegisterCustomer>> SearchCustomersAsync(
        string? search,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, _) = RequireActiveSession();
        RequireConnected(user, NeedsConnectionMessaging);
        return WithSessionAsync(token => headOffice.SearchCustomersAsync(
            Server, token, deviceId.Value, search, cancellationToken));
    }

    /// <summary>Creates a new customer record.</summary>
    public Task<Guid> CreateCustomerAsync(
        string displayName,
        string? phone,
        string? email,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, _) = RequireActiveSession();
        RequireConnected(user, NeedsConnectionMessaging);
        return WithSessionAsync(token => headOffice.CreateCustomerAsync(
            Server, token, deviceId.Value, displayName, phone, email, tin: null, note: null, cancellationToken));
    }

    /// <summary>
    /// Renders the customer copy for the sale that just completed. Head office
    /// records it as an original print; an offline sale is printed from the
    /// register's own record of it.
    /// </summary>
    public async Task<string> GetOriginalReceiptTextAsync(
        CompletedRegisterSale sale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);
        (RegisterUser user, DeviceId deviceId, _) = RequireActiveSession();

        if (sale.IsOffline || user.IsOffline)
        {
            return await RenderLocalReceiptAsync(sale, copy: false, cancellationToken).ConfigureAwait(false);
        }

        return await WithSessionAsync(token => headOffice.GetSaleReceiptAsync(
                Server, token, deviceId.Value, sale.Id, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Logs and renders a permissioned copy of an earlier receipt.</summary>
    public async Task<string> ReprintReceiptTextAsync(
        CompletedRegisterSale sale,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();

        if (sale.IsOffline || user.IsOffline)
        {
            return await RenderLocalReceiptAsync(sale, copy: true, cancellationToken).ConfigureAwait(false);
        }

        await WithSessionAsync(token => headOffice.LogReprintAsync(
                Server, token, deviceId.Value, sale.Id, locationId.Value, reason, clock.UtcNow, cancellationToken))
            .ConfigureAwait(false);

        return await WithSessionAsync(token => headOffice.GetSaleReceiptAsync(
                Server, token, deviceId.Value, sale.Id, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Describes queued work in words a cashier can act on.</summary>
    /// <param name="backlog">The backlog.</param>
    /// <returns>The description, or an empty string when nothing is waiting.</returns>
    public static string DescribeBacklog(DeviceUploadResult backlog)
    {
        ArgumentNullException.ThrowIfNull(backlog);
        if (backlog.Remaining == 0)
        {
            return string.Empty;
        }

        string waiting = backlog.Remaining == 1
            ? "1 offline record hasn’t reached head office yet."
            : FormattableString.Invariant($"{backlog.Remaining} offline records haven’t reached head office yet.");

        return backlog.WaitingForOthers > 0 && backlog.WaitingFor.Count > 0
            ? waiting + " Some belong to " + string.Join(", ", backlog.WaitingFor)
                + ", and go up the next time they sign in here while connected."
            : waiting;
    }

    /// <summary>
    /// Signs in with head office, downloads the store data, remembers the password
    /// for offline use and sends anything queued.
    /// </summary>
    private async Task SignInOnlineAsync(
        DeviceStoreProfile profile,
        string userName,
        string password,
        CancellationToken cancellationToken,
        UserId? expected = null)
    {
        HeadOfficeSession signedIn = await headOffice
            .SignInAsync(Server, userName.Trim(), password, profile.DeviceId.Value, cancellationToken)
            .ConfigureAwait(false);

        UserId userId = new(signedIn.User.UserId);
        if (expected is { } same && same != userId)
        {
            await headOffice.SignOutAsync(Server, signedIn.RefreshToken, profile.DeviceId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            throw new HeadOfficeException("That password belongs to a different account. Lock the register to switch.");
        }

        IReadOnlyDictionary<Guid, ProductSaleReference> references;
        try
        {
            await DownloadStoreDataAsync(profile, signedIn.AccessToken, cancellationToken).ConfigureAwait(false);
            references = await headOffice
                .GetProductSaleReferencesAsync(Server, signedIn.AccessToken, profile.DeviceId.Value, cancellationToken)
                .ConfigureAwait(false);

            // Head office has just accepted this password here, so this is the
            // one moment the register may learn it for when the link is down.
            await offlineSignIn
                .RememberAsync(userId, userName, signedIn.User.DisplayName, password, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await headOffice.SignOutAsync(Server, signedIn.RefreshToken, profile.DeviceId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        _productReferences = references;
        _unreachableAt = null;
        session.SignIn(userId, profile.DeviceId, profile.LocationId);
        Current = new RegisterUser(
            userId,
            userName.Trim(),
            signedIn.User.DisplayName,
            signedIn.User.Permissions,
            signedIn);

        await UploadPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a head-office call with the current access token, renewing the
    /// session once if head office says the token has expired.
    /// </summary>
    private async Task<T> WithSessionAsync<T>(Func<string, Task<T>> call)
    {
        HeadOfficeSession current = Current?.Session ?? throw new HeadOfficeException(NeedsConnectionMessaging);
        try
        {
            T answer;
            try
            {
                answer = await call(current.AccessToken).ConfigureAwait(false);
            }
            catch (HeadOfficeException ex) when (ex.StatusCode == 401)
            {
                HeadOfficeSession renewed = await RenewSessionAsync(current).ConfigureAwait(false);
                answer = await call(renewed.AccessToken).ConfigureAwait(false);
            }

            _unreachableAt = null;
            return answer;
        }
        catch (HeadOfficeUnreachableException)
        {
            _unreachableAt = clock.UtcNow;
            throw;
        }
    }

    /// <summary>Gets a value indicating whether head office failed to answer moments ago.</summary>
    private bool RecentlyUnreachable
        => _unreachableAt is { } failedAt && clock.UtcNow - failedAt < UnreachableGrace;

    /// <summary>Renews the head-office session, or ends it when head office will not.</summary>
    /// <param name="stale">The session whose access token was refused.</param>
    /// <returns>The renewed session.</returns>
    internal async Task<HeadOfficeSession> RenewSessionAsync(HeadOfficeSession stale)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current?.Session is { } latest && !ReferenceEquals(latest, stale))
            {
                // Someone else renewed it while this call waited.
                return latest;
            }

            if (Current is not { Session: not null } user)
            {
                throw new HeadOfficeException(NeedsConnectionMessaging);
            }

            HeadOfficeSession renewed;
            try
            {
                renewed = await headOffice
                    .RefreshAsync(Server, stale.RefreshToken, session.DeviceId?.Value, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (HeadOfficeException ex) when (ex is not HeadOfficeUnreachableException)
            {
                throw new HeadOfficeException(
                    "Your session with head office has ended. Lock the register and sign in again.", ex);
            }

            Current = user with { Session = renewed, Permissions = renewed.User.Permissions };
            return renewed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Gets a current access token for the office console, renewing it when asked to.</summary>
    /// <param name="refused">The token head office just refused, or null for the current one.</param>
    /// <returns>The token, or null when nobody is signed in with head office.</returns>
    public async Task<string?> GetAccessTokenAsync(string? refused = null)
    {
        if (Current?.Session is not { } current)
        {
            return null;
        }

        if (refused is null || !string.Equals(current.AccessToken, refused, StringComparison.Ordinal))
        {
            return current.AccessToken;
        }

        return (await RenewSessionAsync(current).ConfigureAwait(false)).AccessToken;
    }

    private async Task<string> RenderLocalReceiptAsync(
        CompletedRegisterSale sale,
        bool copy,
        CancellationToken cancellationToken)
    {
        DeviceLocalSale local = await offlineSales.FindAsync(sale.Number, cancellationToken).ConfigureAwait(false)
            ?? throw new HeadOfficeException(
                "This receipt is held at head office, which can’t be reached right now. Try again once connected.");

        (string storeName, _) = await GetStoreIdentityAsync(cancellationToken).ConfigureAwait(false);
        return OfflineReceipt.Render(local, storeName, copy);
    }

    private async Task<DocumentNumber> NextNumberAsync(
        DocumentType type,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        IDocumentNumberGenerator generator = scope.ServiceProvider.GetRequiredService<IDocumentNumberGenerator>();
        return await generator.NextAsync(type, cancellationToken).ConfigureAwait(false);
    }

    private (RegisterUser User, DeviceId DeviceId, LocationId LocationId) RequireActiveSession()
    {
        RegisterUser user = Current ?? throw new HeadOfficeException("Sign in before using the till.");
        DeviceId deviceId = session.DeviceId ?? throw new HeadOfficeException("This register has no device identity.");
        LocationId locationId = session.LocationId ?? throw new HeadOfficeException("This register has no store identity.");
        return (user, deviceId, locationId);
    }

    private static void RequireConnected(RegisterUser user, string message)
    {
        if (user.IsOffline)
        {
            throw new HeadOfficeException(message);
        }
    }

    private static string DescribeOfflineRefusal(Error error) => error.Code switch
    {
        "auth.permission_denied" =>
            "Your offline access on this register doesn’t allow opening a shift, or it has expired. " +
            "Connect to head office and sign in to renew it.",
        _ => error.Message,
    };

    private async Task DownloadStoreDataAsync(
        DeviceStoreProfile profile, string accessToken, CancellationToken cancellationToken)
    {
        SyncBaselineResponse baseline = await headOffice
            .GetBaselineAsync(Server, accessToken, profile.DeviceId.Value, cancellationToken)
            .ConfigureAwait(false);

        Result<ChangeFeedBaseline> read = ChangeFeedWire.ReadBaseline(
            baseline.Cursor, baseline.Items.Select(i => (i.Type, i.Payload)));
        if (read.IsFailure)
        {
            throw new HeadOfficeException("The store data from head office could not be read: " + read.Error.Message);
        }

        Result<ChangeFeedApplyOutcome> applied = await applier
            .ReplaceBaselineAsync(read.Value, cancellationToken)
            .ConfigureAwait(false);
        if (applied.IsFailure)
        {
            throw new HeadOfficeException("The store data from head office was refused: " + applied.Error.Message);
        }
    }

    private async Task<DeviceStoreProfile?> ReadProfileAsync(CancellationToken cancellationToken)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.DeviceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The receipt a register prints for a sale it completed without head office.
/// It says so, because the VAT breakdown and the final posting are head
/// office's to make when the sale reaches it.
/// </summary>
public static class OfflineReceipt
{
    /// <summary>Renders an offline sale as printable plain text.</summary>
    /// <param name="sale">The sale.</param>
    /// <param name="storeName">The store's display name.</param>
    /// <param name="copy">True for a reprint.</param>
    /// <returns>The receipt text, LF-separated.</returns>
    public static string Render(DeviceLocalSale sale, string storeName, bool copy)
    {
        ArgumentNullException.ThrowIfNull(sale);
        CultureInfo money = CultureInfo.InvariantCulture;

        StringBuilder text = new();
        text.Append(copy ? "SALE RECEIPT (COPY)" : "SALE RECEIPT").Append('\n');
        text.Append(new string('=', Math.Max(8, sale.Number.Length))).Append('\n');
        text.Append(sale.Number).Append('\n');
        text.Append("Sold: ")
            .Append(sale.CompletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append('\n');
        text.Append("Location: ").Append(storeName).Append('\n');
        text.Append("Cashier: ").Append(sale.CashierName).Append('\n');
        if (!string.IsNullOrWhiteSpace(sale.CustomerName))
        {
            text.Append("Customer: ").Append(sale.CustomerName).Append('\n');
        }

        text.Append('\n');
        foreach (DeviceLocalSaleLine line in sale.ReadLines())
        {
            text.Append(line.Name).Append('\n');
            text.Append("  ")
                .Append(line.Quantity.ToString("0.###", money))
                .Append(" x ")
                .Append(line.UnitPrice.ToString("N2", money))
                .Append(" = ")
                .Append(line.LineTotal.ToString("N2", money))
                .Append('\n');
        }

        text.Append('\n');
        text.Append("TOTAL ").Append(sale.Currency).Append(' ').Append(sale.NetTotal.ToString("N2", money)).Append('\n');
        foreach (DeviceLocalSalePayment payment in sale.ReadPayments())
        {
            text.Append(payment.Method).Append(' ').Append(payment.Amount.ToString("N2", money)).Append('\n');
            if (payment.Tendered is { } tendered)
            {
                text.Append("  Tendered ").Append(tendered.ToString("N2", money)).Append('\n');
                text.Append("  Change ").Append(payment.Change.ToString("N2", money)).Append('\n');
            }
        }

        text.Append('\n');
        text.Append("Recorded offline on this register.").Append('\n');
        text.Append("VAT is itemized once head office receives the sale.").Append('\n');
        text.Append("Thank you.").Append('\n');
        return text.ToString();
    }
}
