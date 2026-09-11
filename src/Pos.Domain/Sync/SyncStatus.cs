namespace Pos.Domain.Sync;

/// <summary>
/// The upload state of a locally created record, as tracked by the device.
/// Persisted as <see cref="short"/>; values must never be renumbered.
/// </summary>
public enum SyncStatus
{
    /// <summary>Created locally and queued for upload.</summary>
    Pending = 0,

    /// <summary>Currently being uploaded.</summary>
    Sending = 1,

    /// <summary>Accepted by the server.</summary>
    Synchronized = 2,

    /// <summary>Upload failed and is awaiting retry.</summary>
    Failed = 3,

    /// <summary>Accepted but parked for a human decision.</summary>
    RequiresReview = 4,

    /// <summary>Rejected because it conflicts with authoritative server state.</summary>
    Conflict = 5,

    /// <summary>
    /// Created directly on the server, so no upload applies. This is the value
    /// for every movement posted by the API itself.
    /// </summary>
    NotApplicable = 6,
}

/// <summary>
/// The server's verdict on a business event. Recorded on the movement so that a
/// reviewer can find everything the server accepted conditionally.
/// Persisted as <see cref="short"/>; values must never be renumbered.
/// </summary>
public enum ServerProcessingStatus
{
    /// <summary>Validated and applied normally.</summary>
    Accepted = 0,

    /// <summary>A repeat of an event already applied; the original result stands.</summary>
    Duplicate = 1,

    /// <summary>Refused. No effect was applied.</summary>
    Rejected = 2,

    /// <summary>
    /// Applied because the physical movement really happened, but flagged
    /// because authority, stock or master data did not line up.
    /// </summary>
    RequiresReview = 3,

    /// <summary>Refused because it contradicts authoritative state.</summary>
    Conflict = 4,

    /// <summary>Applied and later reversed by a compensating group.</summary>
    Reversed = 5,
}
