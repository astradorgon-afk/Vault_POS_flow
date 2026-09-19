using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Client.Storage;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Sync;

namespace Pos.Client.Services;

/// <summary>Who is signed in at this register.</summary>
/// <param name="UserId">The user.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="Session">The head-office session they signed in with.</param>
public sealed record RegisterUser(UserId UserId, string DisplayName, HeadOfficeSession Session);

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
public sealed class RegisterService(
    HeadOfficeClient headOffice,
    DeviceKeyStore keys,
    DeviceDatabaseInitializer database,
    ChangeFeedApplier applier,
    DeviceSession session,
    ISystemClock clock,
    IPreferences preferences,
    IServiceScopeFactory scopes)
{
    private const string ServerPreference = "vaultflow.headoffice.address";
    private IReadOnlyDictionary<Guid, ProductSaleReference> productReferences =
        new Dictionary<Guid, ProductSaleReference>();

    /// <summary>The address a development register points at until a manager changes it.</summary>
    public static readonly Uri DefaultServer = new("http://localhost:5177/");

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

        HeadOfficeSession signedIn = await headOffice
            .SignInAsync(Server, userName.Trim(), password, profile.DeviceId.Value, cancellationToken)
            .ConfigureAwait(false);

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
        catch
        {
            await headOffice.SignOutAsync(Server, signedIn.RefreshToken, profile.DeviceId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        session.SignIn(user.UserId, profile.DeviceId, profile.LocationId);
        Current = user;
    }

    /// <summary>Downloads the store's data again with the current session.</summary>
    public async Task RefreshStoreDataAsync(CancellationToken cancellationToken = default)
    {
        RegisterUser user = Current ?? throw new HeadOfficeException("Sign in to download store data.");
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
    }

    /// <summary>Locks the register. Anything waiting to be sent stays on the device.</summary>
    public async Task SignOutAsync()
    {
        RegisterUser? user = Current;
        Current = null;
        productReferences = new Dictionary<Guid, ProductSaleReference>();
        DeviceId? device = session.DeviceId;
        session.SignOut();

        if (user is not null)
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

    /// <summary>Loads the current shift and server-owned checkout facts for this register.</summary>
    public Task<RegisterCheckoutContext> GetCheckoutContextAsync(CancellationToken cancellationToken = default)
    {
        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        return headOffice.GetCheckoutContextAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            locationId.Value,
            cancellationToken);
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

    /// <summary>Posts a fully paid cart through the server sale, ledger and audit transaction.</summary>
    public async Task<CompletedRegisterSale> CompleteSaleAsync(
        Guid shiftId,
        DateOnly businessDate,
        IReadOnlyList<RegisterSaleLine> lines,
        IReadOnlyList<RegisterSalePayment> payments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(payments);

        (RegisterUser user, DeviceId deviceId, LocationId locationId) = RequireActiveSession();
        DocumentNumber number = await NextNumberAsync(DocumentType.Sale, cancellationToken).ConfigureAwait(false);
        Guid saleId = await headOffice.CompleteSaleAsync(
            Server,
            user.Session.AccessToken,
            deviceId.Value,
            number.Value,
            Guid.CreateVersion7(),
            locationId.Value,
            shiftId,
            businessDate,
            clock.UtcNow,
            lines,
            payments,
            cancellationToken).ConfigureAwait(false);

        return new CompletedRegisterSale(saleId, number.Value);
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
