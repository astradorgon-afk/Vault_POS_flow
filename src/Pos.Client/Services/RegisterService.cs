using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Sales;
using Pos.Client.Storage;
using Pos.Domain.Common;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Sync;

// MAUI puts its own IDispatcher (UI-thread dispatching) in every file's scope.
using IDispatcher = Pos.Application.Common.Messaging.IDispatcher;

namespace Pos.Client.Services;

/// <summary>Who is signed in at this register.</summary>
/// <param name="UserId">The user.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="Session">
/// The head-office session they signed in with. An offline session carries no
/// tokens: it was verified on the device and can reach nothing on the server.
/// </param>
/// <param name="Offline">Whether they signed in on the device because head office could not be reached.</param>
public sealed record RegisterUser(UserId UserId, string DisplayName, HeadOfficeSession Session, bool Offline = false);

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
/// device profile, and each sign-in downloads the store's data and the user's
/// offline authority into the encrypted store.
/// </summary>
/// <param name="headOffice">The head-office client.</param>
/// <param name="keys">The register's key pair.</param>
/// <param name="database">The encrypted device store.</param>
/// <param name="applier">The only writer of downloaded store data.</param>
/// <param name="session">The register's session.</param>
/// <param name="clock">The device clock.</param>
/// <param name="preferences">Where the head-office address is remembered.</param>
/// <param name="scopes">Creates a short-lived scope for the device's atomic document-number generator.</param>
/// <param name="credentials">The offline sign-in verifiers.</param>
/// <param name="cache">What the register keeps from its last conversation with head office.</param>
/// <remarks>
/// When head office cannot be reached the register keeps trading: someone who
/// has signed in here before signs in against the device, a shift opens on the
/// device, and cash sales are queued as business events in the encrypted
/// outbox. A background loop reconnects when head office answers again and
/// uploads the queue in device order (OFFLINE_SYNC.md §3), where the server
/// replays each event and re-checks prices and authority.
/// </remarks>
public sealed class RegisterService(
    HeadOfficeClient headOffice,
    DeviceKeyStore keys,
    DeviceDatabaseInitializer database,
    ChangeFeedApplier applier,
    DeviceSession session,
    ISystemClock clock,
    IPreferences preferences,
    IServiceScopeFactory scopes,
    OfflineCredentialStore credentials,
    OfflineRegisterCache cache) : IDisposable
{
    private const string ServerPreference = "vaultflow.headoffice.address";
    private const int UploadBatchSize = 100;

    /// <summary>How many sales one lookup shows: a busy store rings up several
    /// hundred a day, so older ones are found by receipt number.</summary>
    private const int SaleSearchLimit = 100;
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeferredRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim syncGate = new(1, 1);
    private IReadOnlyDictionary<Guid, ProductSaleReference> productReferences =
        new Dictionary<Guid, ProductSaleReference>();
    private CancellationTokenSource? syncLoop;

    // Held only while an offline session is open, and only in memory, so the
    // register can sign the same person in to head office the moment it
    // answers again and upload what they sold. Cleared on reconnect and sign-out.
    private (string UserName, string Password)? pendingReconnect;

    /// <summary>The address a development register points at until a manager changes it.</summary>
    public static readonly Uri DefaultServer = new("http://localhost:5177/");

    /// <summary>
    /// Gets a value indicating whether <see cref="CompleteSaleAsync"/> can run while
    /// head office is unreachable. Cash sales can: they are queued as
    /// <c>SaleCompleted</c> events and replayed by head office when it answers
    /// again. Card and e-wallet payments still need the payment provider.
    /// </summary>
    public const bool OfflineSaleCompletionSupported = true;

    /// <summary>What a cashier is told when the register is offline.</summary>
    public const string OfflineSaleMessaging =
        "Head office can’t be reached. Keep selling for cash: sales are saved on this register " +
        "and sent automatically when the connection is back. Card, e-wallet, returns and shift close need a connection.";

    /// <summary>What a cashier is told when the checkout context cannot be loaded.</summary>
    public const string ContextUnavailableMessaging =
        "This register could not load its shift and business date.";

    /// <summary>What a cashier is told when an online-only action is tried offline.</summary>
    public const string NeedsConnectionMessaging =
        "This needs a connection to head office. It will be available again once the register reconnects.";

    /// <summary>Raised when the session goes offline or back online, or queued work is uploaded.</summary>
    public event Action? StateChanged;

    /// <summary>Gets a value indicating whether the signed-in session is working offline.</summary>
    public bool IsOffline => Current?.Offline == true;

    /// <summary>Gets the last upload problem, if the most recent attempt had one.</summary>
    public string? LastSyncProblem { get; private set; }

    /// <summary>Gets the head-office address this register uses.</summary>
    public Uri Server =>
        Uri.TryCreate(preferences.Get(ServerPreference, string.Empty), UriKind.Absolute, out Uri? saved)
            ? saved
            : DefaultServer;

    /// <summary>Gets who is signed in, or null when the register is locked.</summary>
    public RegisterUser? Current { get; private set; }

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
    /// Signs someone in at this register, then downloads the store's data and
    /// their offline authority before the session is opened.
    /// </summary>
    public async Task SignInAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        DeviceStoreProfile profile = await ReadProfileAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new HeadOfficeException("This register is not enrolled yet.");

        HeadOfficeSession signedIn;
        try
        {
            signedIn = await headOffice
                .SignInAsync(Server, userName.Trim(), password, profile.DeviceId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HeadOfficeException ex) when (ex.IsUnreachable)
        {
            // No answer at all is not a refusal: let someone who has signed in
            // here before keep the shop open on what the device already holds.
            await SignInOfflineAsync(profile, userName, password, cancellationToken).ConfigureAwait(false);
            return;
        }

        RegisterUser user = new(new UserId(signedIn.User.UserId), signedIn.User.DisplayName, signedIn);
        try
        {
            await DownloadStoreDataAsync(profile, signedIn.AccessToken, cancellationToken).ConfigureAwait(false);
            productReferences = await headOffice
                .GetProductSaleReferencesAsync(
                    Server,
                    signedIn.AccessToken,
                    profile.DeviceId.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HeadOfficeException ex) when (ex.IsUnreachable)
        {
            // Head office accepted the password, then stopped answering part-way
            // through the download. The device still holds the last store data.
            await headOffice.SignOutAsync(Server, signedIn.RefreshToken, profile.DeviceId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            await credentials.RememberAsync(userName, password, signedIn.User, clock.UtcNow).ConfigureAwait(false);
            await SignInOfflineAsync(profile, userName, password, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch
        {
            await headOffice.SignOutAsync(Server, signedIn.RefreshToken, profile.DeviceId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        await credentials.RememberAsync(userName, password, signedIn.User, clock.UtcNow).ConfigureAwait(false);
        await cache.SaveReferencesAsync(productReferences, cancellationToken).ConfigureAwait(false);

        session.SignIn(user.UserId, profile.DeviceId, profile.LocationId);
        Current = user;
        pendingReconnect = null;

        // Anything sold while the register was offline goes up before the till
        // asks head office for its shift, so a shift opened offline is known there.
        await UploadPendingQuietlyAsync(cancellationToken).ConfigureAwait(false);
        StartSyncLoop();
    }

    private async Task SignInOfflineAsync(
        DeviceStoreProfile profile,
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        OfflineSignInResult verified = await credentials
            .VerifyAsync(userName, password, clock.UtcNow)
            .ConfigureAwait(false);
        if (verified.User is not { } offlineUser)
        {
            throw new HeadOfficeException(verified.Reason ?? "The username or password is incorrect.");
        }

        productReferences = await cache.LoadReferencesAsync(cancellationToken).ConfigureAwait(false);

        RegisterUser user = new(
            new UserId(offlineUser.UserId),
            offlineUser.DisplayName,
            new HeadOfficeSession(string.Empty, string.Empty, offlineUser),
            Offline: true);

        session.SignIn(user.UserId, profile.DeviceId, profile.LocationId);
        Current = user;
        pendingReconnect = (userName, password);
        StartSyncLoop();
    }

    /// <summary>Downloads the store's data again with the current session.</summary>
    public async Task RefreshStoreDataAsync(CancellationToken cancellationToken = default)
    {
        RegisterUser user = Current ?? throw new HeadOfficeException("Sign in to download store data.");
        if (user.Offline)
        {
            // Try to come back online first; the reconnect downloads fresh data itself.
            await SyncNowAsync(cancellationToken).ConfigureAwait(false);
            if (IsOffline)
            {
                throw new HeadOfficeException(NeedsConnectionMessaging);
            }

            return;
        }
        DeviceStoreProfile profile = await ReadProfileAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new HeadOfficeException("This register is not enrolled yet.");

        await DownloadStoreDataAsync(profile, user.Session.AccessToken, cancellationToken).ConfigureAwait(false);
        productReferences = await headOffice
            .GetProductSaleReferencesAsync(
                Server,
                user.Session.AccessToken,
                profile.DeviceId.Value,
                cancellationToken)
            .ConfigureAwait(false);
        await cache.SaveReferencesAsync(productReferences, cancellationToken).ConfigureAwait(false);
        await UploadPendingQuietlyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Locks the register. Anything waiting to be sent stays on the device.</summary>
    public async Task SignOutAsync()
    {
        StopSyncLoop();

        // Last chance to send the queue under this person's session: head office
        // accepts an event only from the user who produced it.
        await UploadPendingQuietlyAsync(CancellationToken.None).ConfigureAwait(false);

        RegisterUser? user = Current;
        Current = null;
        pendingReconnect = null;
        productReferences = new Dictionary<Guid, ProductSaleReference>();
        DeviceId? device = session.DeviceId;
        session.SignOut();

        if (user is { Offline: false })
        {
            await headOffice.SignOutAsync(Server, user.Session.RefreshToken, device?.Value, CancellationToken.None)
                .ConfigureAwait(false);
        }
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

                productReferences.TryGetValue(p.Id.Value, out ProductSaleReference? reference);
                return new CatalogueItem(
                    p.Id.Value,
                    reference?.BaseUnitOfMeasureId,
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
    /// The store's receipt header and footer as head office last sent them, so
    /// a receipt printed offline carries the same branch details and return
    /// policy as one head office renders.
    /// </summary>
    public async Task<(string Header, string Footer)> GetReceiptTextAsync(CancellationToken cancellationToken = default)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeviceStoreProfile? profile = await context.DeviceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return (string.Empty, string.Empty);
        }

        string? settingsJson = await context.Locations.AsNoTracking()
            .Where(l => l.Id == profile.LocationId)
            .Select(l => l.SettingsJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        Pos.Domain.Organizations.LocationSettings settings = Pos.Domain.Organizations.LocationSettings.FromJson(settingsJson);
        return (settings.ReceiptHeader, settings.ReceiptFooter);
    }

    /// <summary>
    /// Loads the current shift and checkout facts for this register: from head
    /// office when it answers, otherwise from what the device holds.
    /// </summary>
    public async Task<RegisterCheckoutContext> GetCheckoutContextAsync(CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        if (user.Offline)
        {
            return await GetOfflineContextAsync(user, deviceId, locationId, cancellationToken).ConfigureAwait(false);
        }

        RegisterCheckoutContext online;
        try
        {
            online = await headOffice.GetCheckoutContextAsync(
                Server,
                user.Session.AccessToken,
                deviceId.Value,
                locationId.Value,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HeadOfficeException ex) when (ex.IsUnreachable)
        {
            return await GetOfflineContextAsync(user, deviceId, locationId, cancellationToken).ConfigureAwait(false);
        }

        cache.SaveContext(deviceId.Value, online, clock.UtcNow);

        // A shift opened on the device whose event has not reached head office
        // yet is still the open shift; opening a second one would conflict.
        if (online.OpenShift is null)
        {
            RegisterOpenShift? unsent = await ReadUnsentLocalShiftAsync(user, deviceId, cancellationToken)
                .ConfigureAwait(false);
            if (unsent is not null)
            {
                return online with { OpenShift = unsent };
            }
        }

        return online;
    }

    /// <summary>Opens a shift using this device's gap-safe SHF counter.</summary>
    public async Task<RegisterCheckoutContext> OpenShiftAsync(
        decimal openingFloat,
        DateOnly businessDate,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        DocumentNumber number = await NextNumberAsync(DocumentType.CashierShift, cancellationToken)
            .ConfigureAwait(false);

        if (!user.Offline)
        {
            try
            {
                await headOffice.OpenShiftAsync(
                    Server,
                    user.Session.AccessToken,
                    deviceId.Value,
                    number.Value,
                    locationId.Value,
                    businessDate,
                    openingFloat,
                    cancellationToken).ConfigureAwait(false);

                return await GetCheckoutContextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HeadOfficeException ex) when (ex.IsUnreachable)
            {
                // Open it on the device instead, under the same number, so head
                // office refuses a duplicate if the first attempt did land.
            }
        }

        await OpenShiftOnDeviceAsync(number, locationId, businessDate, openingFloat, cancellationToken)
            .ConfigureAwait(false);
        return await GetOfflineContextAsync(user, deviceId, locationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Posts a fully paid cart through the server sale, ledger and audit transaction.</summary>
    public async Task<CompletedRegisterSale> CompleteSaleAsync(
        Guid shiftId,
        DateOnly businessDate,
        Guid? customerId,
        IReadOnlyList<RegisterSaleLine> lines,
        IReadOnlyList<RegisterSalePayment> payments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(payments);

        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        bool cashOnly = payments.All(p => p.Method == PaymentMethod.Cash);
        if (user.Offline && !cashOnly)
        {
            throw new HeadOfficeException(
                "Card and e-wallet payments need head office. Take this payment in cash, or wait for the connection.");
        }

        DocumentNumber number = await NextNumberAsync(DocumentType.Sale, cancellationToken).ConfigureAwait(false);

        if (!user.Offline)
        {
            try
            {
                Guid saleId = await headOffice.CompleteSaleAsync(
                    Server,
                    user.Session.AccessToken,
                    deviceId.Value,
                    number.Value,
                    Guid.CreateVersion7(),
                    locationId.Value,
                    shiftId,
                    customerId,
                    businessDate,
                    clock.UtcNow,
                    lines,
                    payments,
                    cancellationToken).ConfigureAwait(false);

                return new CompletedRegisterSale(saleId, number.Value);
            }
            catch (HeadOfficeException ex) when (ex.IsUnreachable && cashOnly)
            {
                // Queue it under the same number: if the first attempt did land,
                // head office refuses the replay as a duplicate instead of
                // recording the sale twice.
            }
        }

        Guid eventId = await QueueSaleAsync(
            number, user, deviceId, locationId, shiftId, customerId, businessDate, lines, payments, cancellationToken)
            .ConfigureAwait(false);

        return new CompletedRegisterSale(eventId, number.Value, QueuedOffline: true);
    }

    /// <summary>Gets the X-REPORT facts for the shift open on this register.</summary>
    public async Task<RegisterShiftSummary> GetShiftSummaryAsync(
        Guid shiftId,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, _) = RequireOnlineSession();
        return await headOffice.GetShiftSummaryAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            shiftId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Declares and counts the drawer, then closes the shift.</summary>
    public async Task CloseShiftAsync(
        Guid shiftId,
        decimal declaredCash,
        decimal countedCash,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireOnlineSession();
        await headOffice.CloseShiftAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            shiftId,
            locationId.Value,
            declaredCash,
            countedCash,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds this register's store's newest sales in a business-date
    /// window, optionally by part of the receipt number.</summary>
    public async Task<IReadOnlyList<RegisterSaleSummary>> SearchSalesAsync(
        DateOnly? from,
        DateOnly? to,
        string? number = null,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireOnlineSession();
        return await headOffice.SearchSalesAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            locationId.Value,
            from,
            to,
            number,
            SaleSearchLimit,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads a completed sale's lines, ready for a return.</summary>
    public async Task<RegisterSaleDetail> GetSaleDetailAsync(
        Guid saleId,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, _) = RequireOnlineSession();
        return await headOffice.GetSaleDetailAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            saleId,
            cancellationToken).ConfigureAwait(false);
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

        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireOnlineSession();
        DocumentNumber number = await NextNumberAsync(DocumentType.SalesReturn, cancellationToken).ConfigureAwait(false);
        Guid returnId = await headOffice.CreateReturnAsync(
            Server,
            user.Session.AccessToken,
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
            cancellationToken).ConfigureAwait(false);

        return new AcceptedRegisterReturn(returnId, number.Value);
    }

    /// <summary>Issues a refund against a return through the open shift.</summary>
    public async Task<Guid> RefundReturnAsync(
        Guid returnId,
        Guid? saleId,
        Guid shiftId,
        PaymentMethod method,
        decimal amount,
        decimal? tendered,
        string? providerReference,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireOnlineSession();
        return await headOffice.RefundReturnAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            returnId,
            saleId,
            Guid.CreateVersion7(),
            locationId.Value,
            shiftId,
            method,
            amount,
            tendered,
            providerReference,
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Searches customer records by name, phone or email.</summary>
    public async Task<IReadOnlyList<RegisterCustomer>> SearchCustomersAsync(
        string? search,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, _) = RequireOnlineSession();
        return await headOffice.SearchCustomersAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            search,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a new customer record.</summary>
    public async Task<Guid> CreateCustomerAsync(
        string displayName,
        string? phone,
        string? email,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, _) = RequireOnlineSession();
        return await headOffice.CreateCustomerAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            displayName,
            phone,
            email,
            tin: null,
            note: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders the customer copy for the sale that just completed. The receipt
    /// endpoint records this as an original print, not as a privileged reprint.
    /// </summary>
    public async Task<string> GetOriginalReceiptTextAsync(
        Guid saleId,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, _) = RequireOnlineSession();
        return await headOffice.GetSaleReceiptAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            saleId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Logs and renders a permissioned copy of an earlier receipt.</summary>
    public async Task<string> ReprintReceiptTextAsync(
        Guid saleId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireOnlineSession();
        await headOffice.LogReprintAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            saleId,
            locationId.Value,
            reason,
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);

        return await headOffice.GetSaleReceiptAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            saleId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DocumentNumber> NextNumberAsync(
        DocumentType type,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        IDocumentNumberGenerator generator = scope.ServiceProvider.GetRequiredService<IDocumentNumberGenerator>();
        return await generator.NextAsync(type, cancellationToken).ConfigureAwait(false);
    }

    private (RegisterUser User, DeviceId DeviceId, LocationId LocationId) RequireOnlineSession()
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        return user.Offline
            ? throw new HeadOfficeException(NeedsConnectionMessaging)
            : (user, deviceId, locationId);
    }

    private (RegisterUser User, DeviceId DeviceId, LocationId LocationId) RequireActiveSession()
    {
        RegisterUser user = Current ?? throw new HeadOfficeException("Sign in before using the till.");
        DeviceId deviceId = session.DeviceId ?? throw new HeadOfficeException("This register has no device identity.");
        LocationId locationId = session.LocationId ?? throw new HeadOfficeException("This register has no store identity.");
        return (user, deviceId, locationId);
    }

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

    /// <summary>
    /// Tries to reach head office now: signs an offline session back in, then
    /// uploads whatever is queued. Never throws; a problem is kept in
    /// <see cref="LastSyncProblem"/> for the status strip.
    /// </summary>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>A task that completes when the attempt is over.</returns>
    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (!await syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (Current is { Offline: true })
            {
                await TryReconnectAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Current is { Offline: false })
            {
                await UploadPendingAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HeadOfficeException ex)
        {
            LastSyncProblem = ex.Message;
        }
        catch (DbUpdateException ex)
        {
            LastSyncProblem = "The upload queue could not be updated: " + ex.Message;
        }
        finally
        {
            syncGate.Release();
        }

        if (Current is not null)
        {
            StateChanged?.Invoke();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopSyncLoop();
        syncGate.Dispose();
    }

    private void StartSyncLoop()
    {
        StopSyncLoop();
        CancellationTokenSource loop = new();
        syncLoop = loop;
        _ = RunSyncLoopAsync(loop);
    }

    private void StopSyncLoop() => Interlocked.Exchange(ref syncLoop, null)?.Cancel();

    private async Task RunSyncLoopAsync(CancellationTokenSource loop)
    {
        CancellationToken token = loop.Token;
        try
        {
            using PeriodicTimer timer = new(SyncInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await SyncNowAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Signed out.
        }
        finally
        {
            loop.Dispose();
        }
    }

    private async Task UploadPendingQuietlyAsync(CancellationToken cancellationToken)
    {
        await syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UploadPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HeadOfficeException ex)
        {
            LastSyncProblem = ex.Message;
        }
        catch (DbUpdateException ex)
        {
            LastSyncProblem = "The upload queue could not be updated: " + ex.Message;
        }
        finally
        {
            syncGate.Release();
        }
    }

    // Signs an offline session in to head office once it answers again, with
    // the credentials the cashier typed at the start of that session.
    private async Task TryReconnectAsync(CancellationToken cancellationToken)
    {
        if (pendingReconnect is not { } login || Current is not { Offline: true } offline)
        {
            return;
        }

        DeviceStoreProfile? profile = await ReadProfileAsync(cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return;
        }

        HeadOfficeSession signedIn;
        try
        {
            signedIn = await headOffice
                .SignInAsync(Server, login.UserName.Trim(), login.Password, profile.DeviceId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HeadOfficeException ex) when (ex.IsUnreachable)
        {
            return;
        }
        catch (HeadOfficeException ex)
        {
            // Head office answered and said no: the password changed or the
            // account was disabled. Stop trying; sales stay queued on the device
            // until someone head office accepts signs in here.
            pendingReconnect = null;
            throw new HeadOfficeException(
                "Head office refused this account when the register reconnected (" + ex.Message +
                "). Queued sales stay on this register. Lock it and sign in again.");
        }

        if (signedIn.User.UserId != offline.UserId.Value)
        {
            await headOffice.SignOutAsync(Server, signedIn.RefreshToken, profile.DeviceId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            pendingReconnect = null;
            return;
        }

        IReadOnlyDictionary<Guid, ProductSaleReference> references;
        try
        {
            await DownloadStoreDataAsync(profile, signedIn.AccessToken, cancellationToken).ConfigureAwait(false);
            references = await headOffice
                .GetProductSaleReferencesAsync(Server, signedIn.AccessToken, profile.DeviceId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HeadOfficeException)
        {
            await headOffice.SignOutAsync(Server, signedIn.RefreshToken, profile.DeviceId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        productReferences = references;
        await credentials.RememberAsync(login.UserName, login.Password, signedIn.User, clock.UtcNow).ConfigureAwait(false);
        await cache.SaveReferencesAsync(references, cancellationToken).ConfigureAwait(false);

        Current = new RegisterUser(offline.UserId, signedIn.User.DisplayName, signedIn);
        pendingReconnect = null;
    }

    // Sends the queue in device order, one batch at a time, as the signed-in
    // user. Head office accepts an event only from the user who produced it, so
    // the batch stops at the first event someone else queued; that one goes up
    // when they sign in here.
    private async Task UploadPendingAsync(CancellationToken cancellationToken)
    {
        if (Current is not { Offline: false } user || session.DeviceId is not { } deviceId)
        {
            return;
        }

        bool refreshed = false;
        for (int round = 0; round < 50; round++)
        {
            await using PosDeviceDbContext context =
                await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            DateTimeOffset now = clock.UtcNow;
            List<OutboxEvent> queue = await context.Outbox
                .Where(e => e.Status == OutboxStatus.Pending
                            || e.Status == OutboxStatus.Sending
                            || (e.Status == OutboxStatus.Failed && e.NextRetryAtUtc != null))
                .OrderBy(e => e.DeviceSequence)
                .Take(UploadBatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            List<OutboxEvent> batch = [.. queue.TakeWhile(e => e.UserId == user.UserId)];
            if (batch.Count == 0)
            {
                LastSyncProblem = queue.Count == 0
                    ? null
                    : "Sales queued by another cashier are waiting; they are sent when that cashier signs in here.";
                return;
            }

            if (!batch[0].IsDue(now))
            {
                return;
            }

            SyncPushRequest request = new(
                deviceId.Value,
                now,
                Environment.TickCount64,
                [.. batch.Select(ToWire)]);

            SyncPushResponse response;
            try
            {
                response = await headOffice
                    .PushEventsAsync(Server, user.Session.AccessToken, deviceId.Value, request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HeadOfficeException ex) when (ex.StatusCode == 401 && !refreshed)
            {
                // Access tokens are short-lived and an outage usually outlasts
                // one. Exchange the refresh token once and send the batch again.
                refreshed = true;
                user = await RefreshSessionAsync(user, deviceId, cancellationToken).ConfigureAwait(false);
                round--;
                continue;
            }
            catch (HeadOfficeException ex)
            {
                foreach (OutboxEvent queued in batch)
                {
                    queued.RecordTransportFailure(now, ex.Message);
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                LastSyncProblem = ex.Message;
                return;
            }

            Dictionary<Guid, SyncPushEventResult> results = response.Results
                .GroupBy(r => r.EventId)
                .ToDictionary(g => g.Key, g => g.Last());

            bool deferred = false;
            foreach (OutboxEvent queued in batch)
            {
                if (!results.TryGetValue(queued.EventId.Value, out SyncPushEventResult? result))
                {
                    queued.RecordTransportFailure(now, "Head office did not report on this event.");
                    continue;
                }

                string json = JsonSerializer.Serialize(result, ResultJson);
                switch (result.Outcome)
                {
                    case "Accepted":
                    case "Duplicate":
                        queued.RecordServerOutcome(OutboxStatus.Synchronized, now, null, json);
                        break;
                    case "RequiresReview":
                        queued.RecordServerOutcome(OutboxStatus.RequiresReview, now, result.Message, json);
                        break;
                    case "Conflict":
                        queued.RecordServerOutcome(OutboxStatus.Conflict, now, result.Message, json);
                        break;
                    case "Deferred":
                        queued.RecordDeferred(now, now + DeferredRetryDelay, result.Message);
                        deferred = true;
                        break;
                    default:
                        queued.RecordServerOutcome(OutboxStatus.Failed, now, result.Message ?? result.ErrorCode, json);
                        break;
                }
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            LastSyncProblem = null;

            if (deferred || batch.Count < UploadBatchSize)
            {
                return;
            }
        }
    }

    private async Task<RegisterUser> RefreshSessionAsync(
        RegisterUser user,
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        HeadOfficeSession renewed = await headOffice
            .RefreshAsync(Server, user.Session.RefreshToken, deviceId.Value, cancellationToken)
            .ConfigureAwait(false);

        RegisterUser current = user with { Session = renewed };
        if (Current?.UserId == user.UserId)
        {
            Current = current;
        }

        return current;
    }

    private static SyncPushEvent ToWire(OutboxEvent queued)
        => new(
            queued.EventId.Value,
            queued.DeviceSequence,
            queued.Type.ToString(),
            queued.PayloadJson,
            Convert.ToBase64String(queued.PayloadHash),
            queued.UserId.Value,
            queued.LocationId.Value,
            queued.OccurredAtUtc,
            queued.DeviceUptimeTicks,
            queued.CorrelationId.Value);

    // Opens the shift through the same use case head office runs, against the
    // device store. It queues the ShiftOpened event in the same transaction.
    private async Task OpenShiftOnDeviceAsync(
        DocumentNumber number,
        LocationId locationId,
        DateOnly businessDate,
        decimal openingFloat,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<CashierShiftId> opened = await dispatcher
            .SendAsync(new OpenShiftCommand(number, locationId, businessDate, openingFloat), cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            throw new HeadOfficeException(opened.Error.Code == "auth.permission_denied"
                ? "This account can’t open a shift offline: its offline permissions are missing or have expired. " +
                  "Connect to head office and sign in again."
                : opened.Error.Message);
        }

        StateChanged?.Invoke();
    }

    // Queues a cash sale as the business event head office replays: the
    // command input, not a sale row, so prices, tax and stock are re-derived
    // centrally. The event commits with its outbox sequence or not at all.
    private async Task<Guid> QueueSaleAsync(
        DocumentNumber number,
        RegisterUser user,
        DeviceId deviceId,
        LocationId locationId,
        Guid shiftId,
        Guid? customerId,
        DateOnly businessDate,
        IReadOnlyList<RegisterSaleLine> lines,
        IReadOnlyList<RegisterSalePayment> payments,
        CancellationToken cancellationToken)
    {
        await RequireOfflineAuthorityAsync(
            user, locationId, Pos.Application.Identity.Permissions.Sales.Create, cancellationToken).ConfigureAwait(false);

        SaleSyncPayload payload = new(
            number.Value,
            locationId.Value,
            shiftId,
            deviceId.Value,
            user.UserId.Value,
            customerId,
            businessDate,
            clock.UtcNow,
            [.. lines.Select(line => new SaleSyncLine(
                line.ProductId,
                line.Quantity,
                line.UnitOfMeasureId,
                line.Barcode,
                UnitPriceOverride: null,
                PriceOverrideAuthorizedByUserId: null,
                Discount: 0m,
                DiscountAuthorizedByUserId: null,
                AllowExpiredOverride: false,
                ExpiredOverrideReason: null))],
            [.. payments.Select(payment => new SaleSyncPayment(
                payment.Method,
                payment.Amount,
                payment.Tendered,
                payment.ProviderReference))]);

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IDeviceOutbox outbox = scope.ServiceProvider.GetRequiredService<IDeviceOutbox>();

        OutboxEvent queued;
        await using (IUnitOfWorkTransaction transaction =
            await unitOfWork.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            queued = await outbox
                .EnqueueAsync(SyncEventType.SaleCompleted, payload, locationId, cancellationToken)
                .ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        StateChanged?.Invoke();
        return queued.EventId.Value;
    }

    // Offline never widens authority: a queued sale needs the permission in the
    // snapshot head office issued to this user for this store, still in date.
    private async Task RequireOfflineAuthorityAsync(
        RegisterUser user,
        LocationId locationId,
        string permission,
        CancellationToken cancellationToken)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<DevicePermissionSnapshot> snapshot = await context.PermissionSnapshots
            .AsNoTracking()
            .Where(p => p.UserId == user.UserId && p.Permission == permission)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;
        bool granted = snapshot.Exists(p =>
            p.ExpiresAtUtc > now && (p.LocationId is null || p.LocationId == locationId));

        if (!granted)
        {
            throw new HeadOfficeException(
                "This account can’t sell offline on this register: its offline permissions are missing or have expired. " +
                "Connect to head office and sign in again.");
        }
    }

    // The checkout context from what the device holds: its own open shift if
    // head office has not seen it yet, otherwise the last context head office
    // returned, and today's date in the store's time zone.
    private async Task<RegisterCheckoutContext> GetOfflineContextAsync(
        RegisterUser user,
        DeviceId deviceId,
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CachedCheckoutContext? cached = cache.LoadContext(deviceId.Value);
        CashierShift? local = await ReadLocalOpenShiftAsync(context, deviceId, cancellationToken).ConfigureAwait(false);

        RegisterOpenShift? open = cached?.Context.OpenShift;
        if (local is not null
            && (cached is null
                || cached.FetchedAtUtc < local.OpenedAtUtc
                || await IsUnsentAsync(context, local, cancellationToken).ConfigureAwait(false)))
        {
            open = await DescribeAsync(context, local, user, cancellationToken).ConfigureAwait(false);
        }

        DateOnly businessDate = open?.BusinessDate
            ?? await StoreTodayAsync(context, locationId, cancellationToken).ConfigureAwait(false);

        return new RegisterCheckoutContext(
            businessDate,
            cached?.Context.CashRoundingIncrement ?? 0.01m,
            open);
    }

    private async Task<RegisterOpenShift?> ReadUnsentLocalShiftAsync(
        RegisterUser user,
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CashierShift? local = await ReadLocalOpenShiftAsync(context, deviceId, cancellationToken).ConfigureAwait(false);
        return local is not null && await IsUnsentAsync(context, local, cancellationToken).ConfigureAwait(false)
            ? await DescribeAsync(context, local, user, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static Task<CashierShift?> ReadLocalOpenShiftAsync(
        PosDeviceDbContext context,
        DeviceId deviceId,
        CancellationToken cancellationToken)
        => context.LocalShifts
            .AsNoTracking()
            .Where(s => s.DeviceId == deviceId
                        && (s.Status == ShiftStatus.Open || s.Status == ShiftStatus.Suspended))
            .OrderByDescending(s => s.OpenedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    // Whether head office has yet to accept the event that opened this shift.
    private static async Task<bool> IsUnsentAsync(
        PosDeviceDbContext context,
        CashierShift shift,
        CancellationToken cancellationToken)
    {
        string shiftId = shift.Id.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        List<string> unsent = await context.Outbox
            .AsNoTracking()
            .Where(e => e.Type == SyncEventType.ShiftOpened && e.Status != OutboxStatus.Synchronized)
            .Select(e => e.PayloadJson)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return unsent.Exists(payload => payload.Contains(shiftId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<RegisterOpenShift> DescribeAsync(
        PosDeviceDbContext context,
        CashierShift shift,
        RegisterUser user,
        CancellationToken cancellationToken)
    {
        string cashierName = shift.CashierUserId == user.UserId
            ? user.DisplayName
            : await context.Users
                .AsNoTracking()
                .Where(u => u.Id == shift.CashierUserId)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) ?? "another cashier";

        return new RegisterOpenShift(
            shift.Id.Value,
            shift.Number,
            shift.CashierUserId.Value,
            cashierName,
            shift.BusinessDate,
            shift.OpeningFloat);
    }

    private async Task<DateOnly> StoreTodayAsync(
        PosDeviceDbContext context,
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        string? timeZoneId = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId)
            .Select(l => l.TimeZoneId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset local = now.ToLocalTime();
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
            }
            catch (TimeZoneNotFoundException)
            {
                // The device's own zone is the best remaining guess.
            }
            catch (InvalidTimeZoneException)
            {
                // As above.
            }
        }

        return DateOnly.FromDateTime(local.DateTime);
    }
}
