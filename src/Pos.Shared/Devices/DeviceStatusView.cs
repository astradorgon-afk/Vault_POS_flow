namespace Pos.Shared.Devices;

/// <summary>Whether the device's local store is open and usable.</summary>
public enum DeviceStorageState
{
    /// <summary>The store has not been opened yet, or opening it failed.</summary>
    NotReady = 0,

    /// <summary>The store is open and up to date.</summary>
    Ready = 1,
}

/// <summary>Whether this device has been given an identity and a store.</summary>
public enum DeviceEnrolmentState
{
    /// <summary>The device has no identity yet and cannot trade.</summary>
    NotEnrolled = 0,

    /// <summary>The device is enrolled to a location.</summary>
    Enrolled = 1,
}

/// <summary>Whether the device can currently reach head office.</summary>
public enum DeviceConnectivityState
{
    /// <summary>No connection. Offline work continues; it uploads when the link returns.</summary>
    Offline = 0,

    /// <summary>Connected.</summary>
    Online = 1,
}

/// <summary>Whether the device has ever received store data.</summary>
public enum DeviceSyncState
{
    /// <summary>Nothing has been downloaded yet, so the device has no catalogue.</summary>
    NeverSynchronised = 0,

    /// <summary>Store data has been received at least once.</summary>
    Synchronised = 1,
}

/// <summary>
/// How much of the signed-in user's cached authority is left. Authority is
/// time-bounded on purpose: a device that cannot reach head office must not keep
/// its permissions indefinitely.
/// </summary>
public enum DeviceAuthorityState
{
    /// <summary>Nobody is signed in, or this user has no cached authority.</summary>
    None = 0,

    /// <summary>Cached authority is current.</summary>
    Active = 1,

    /// <summary>Cached authority runs out soon; the device should reconnect.</summary>
    ExpiringSoon = 2,

    /// <summary>Cached authority has run out. The device is read-only until it reconnects.</summary>
    Expired = 3,
}

/// <summary>
/// The one thing most worth saying about a register right now. The order the
/// provider resolves these in is the order they matter on a shop floor, and it
/// is here rather than in a component so it can be tested without rendering
/// anything.
/// </summary>
public enum DeviceStatusConcern
{
    /// <summary>Connected, provisioned and taking sales.</summary>
    Ready = 0,

    /// <summary>Taking sales with no connection, which is normal.</summary>
    WorkingOffline = 1,

    /// <summary>Cached authority runs out within the warning window.</summary>
    AuthorityExpiringSoon = 2,

    /// <summary>Cached authority has run out; the register is read-only.</summary>
    AuthorityExpired = 3,

    /// <summary>Nobody is signed in, so there is no authority to act under.</summary>
    AwaitingSignIn = 4,

    /// <summary>No catalogue has arrived, so nothing can be rung up.</summary>
    AwaitingStoreData = 5,

    /// <summary>The register has no identity yet.</summary>
    NotEnrolled = 6,

    /// <summary>The local store has not opened.</summary>
    StorageNotReady = 7,
}

/// <summary>How loudly the register is asking to be looked at.</summary>
public enum DeviceStatusSeverity
{
    /// <summary>Nothing to do.</summary>
    Normal = 0,

    /// <summary>Working, but someone should act before it stops.</summary>
    Warning = 1,

    /// <summary>Not taking sales.</summary>
    Blocked = 2,
}

/// <summary>
/// What a cashier is shown about the state of their register. It says what the
/// device can and cannot do and what to do about it, and nothing about how any
/// of it works.
/// </summary>
/// <remarks>
/// The contract lives in <c>Pos.Shared</c>, which references nothing, and that
/// is the point: there is no type here that could carry a file path, a
/// connection string, a key, a server address or a feed position. A cashier who
/// photographs this screen for a support ticket gives away nothing, and a screen
/// in a shop cannot become a map of the estate's infrastructure.
/// </remarks>
/// <param name="Storage">Whether the local store is open.</param>
/// <param name="Enrolment">Whether this device has an identity.</param>
/// <param name="DeviceCode">
/// The device's short code, which is printed on its receipts. Shown so a
/// cashier can quote it; it identifies the register, not where its data lives.
/// </param>
/// <param name="LocationName">The store this device belongs to.</param>
/// <param name="Connectivity">Whether head office is reachable.</param>
/// <param name="Sync">Whether store data has ever arrived.</param>
/// <param name="LastSynchronisedUtc">When store data last arrived.</param>
/// <param name="Authority">How much cached authority is left.</param>
/// <param name="AuthorityExpiresUtc">When cached authority runs out.</param>
public sealed record DeviceStatusView(
    DeviceStorageState Storage,
    DeviceEnrolmentState Enrolment,
    string? DeviceCode,
    string? LocationName,
    DeviceConnectivityState Connectivity,
    DeviceSyncState Sync,
    DateTimeOffset? LastSynchronisedUtc,
    DeviceAuthorityState Authority,
    DateTimeOffset? AuthorityExpiresUtc)
{
    /// <summary>
    /// Gets a value indicating whether the register can take a sale. It is
    /// deliberately not the same as "online": selling offline is the point of the
    /// device, and only a store that is not open, an unenrolled register, a
    /// device that has never received its catalogue, or expired authority stops
    /// it.
    /// </summary>
    public bool CanTrade =>
        Storage == DeviceStorageState.Ready
        && Enrolment == DeviceEnrolmentState.Enrolled
        && Sync == DeviceSyncState.Synchronised
        && Authority is DeviceAuthorityState.Active or DeviceAuthorityState.ExpiringSoon;

    /// <summary>
    /// Gets a value indicating whether the register needs someone's attention
    /// now, rather than merely being offline.
    /// </summary>
    public bool NeedsAttention =>
        !CanTrade || Authority == DeviceAuthorityState.ExpiringSoon;

    /// <summary>
    /// Gets the single thing most worth saying. What blocks trading comes first,
    /// starting with the state that has to be fixed earliest, then what will
    /// block it soon, and only then the ordinary offline case.
    /// </summary>
    public DeviceStatusConcern Concern => this switch
    {
        { Storage: DeviceStorageState.NotReady } => DeviceStatusConcern.StorageNotReady,
        { Enrolment: DeviceEnrolmentState.NotEnrolled } => DeviceStatusConcern.NotEnrolled,
        { Sync: DeviceSyncState.NeverSynchronised } => DeviceStatusConcern.AwaitingStoreData,
        { Authority: DeviceAuthorityState.Expired } => DeviceStatusConcern.AuthorityExpired,
        { Authority: DeviceAuthorityState.None } => DeviceStatusConcern.AwaitingSignIn,
        { Authority: DeviceAuthorityState.ExpiringSoon } => DeviceStatusConcern.AuthorityExpiringSoon,
        { Connectivity: DeviceConnectivityState.Offline } => DeviceStatusConcern.WorkingOffline,
        _ => DeviceStatusConcern.Ready,
    };

    /// <summary>Gets how loudly the register is asking to be looked at.</summary>
    public DeviceStatusSeverity Severity => Concern switch
    {
        DeviceStatusConcern.Ready or DeviceStatusConcern.WorkingOffline => DeviceStatusSeverity.Normal,
        DeviceStatusConcern.AuthorityExpiringSoon => DeviceStatusSeverity.Warning,
        _ => DeviceStatusSeverity.Blocked,
    };
}
