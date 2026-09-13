using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Quarantine;

namespace Pos.Application.Quarantine;

/// <summary>
/// Raises a quarantine incident for unauthorized or unknown stock found at a
/// location. The handler resolves known barcodes to products, identifies those
/// lines immediately, and posts the QuarantineEntry ledger group for them.
/// Unknown barcodes are registered or linked later by head office.
/// </summary>
/// <param name="LocationId">The location where the goods were found.</param>
/// <param name="Lines">The lines to quarantine, in the products' base units.</param>
/// <param name="Note">An optional note explaining the finding.</param>
public sealed record CreateQuarantineIncidentCommand(
    LocationId LocationId,
    IReadOnlyList<QuarantineLineSpec> Lines,
    string? Note = null)
    : ICommand<QuarantineIncidentId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Quarantine.Create;
}

/// <summary>
/// Attaches a scene photograph to an incident. The store raising the incident
/// documents what was found; head office sees the photo before dispositioning.
/// Bytes arrive base64-encoded in the request body.
/// </summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="FileName">The original file name.</param>
/// <param name="ContentType">The image MIME type.</param>
/// <param name="Data">The image bytes.</param>
/// <param name="Note">An optional caption.</param>
public sealed record AddQuarantinePhotoCommand(
    QuarantineIncidentId IncidentId,
    string FileName,
    string ContentType,
    byte[] Data,
    string? Note = null)
    : ICommand<QuarantineIncidentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Quarantine.Create;
}

/// <summary>Marks an incident as under active head office review.</summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="Note">What the investigation established, when provided.</param>
public sealed record InvestigateQuarantineIncidentCommand(
    QuarantineIncidentId IncidentId,
    string? Note = null)
    : ICommand<QuarantineIncidentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Quarantine.Investigate;
}

/// <summary>
/// Links a line to an existing catalogue product. The scanned barcode must
/// already belong to that product; the handler posts the QuarantineEntry ledger
/// group that moves the stock from the supplier counterparty into quarantine.
/// </summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="LineNo">The line being identified.</param>
/// <param name="ProductId">The catalogue product the goods were identified as.</param>
/// <param name="BatchId">The lot of the found goods, for batch-tracked products.</param>
/// <param name="Note">An optional identification note.</param>
public sealed record LinkQuarantineProductCommand(
    QuarantineIncidentId IncidentId,
    int LineNo,
    ProductId ProductId,
    BatchId? BatchId = null,
    string? Note = null)
    : ICommand<QuarantineIncidentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Quarantine.Release;
}

/// <summary>
/// Links a line to a product registered for this incident. The endpoint creates
/// the product first, so the product arrives already carrying the scanned
/// barcode; this command performs the identification. It is dispatched by the
/// same endpoint under the caller's authority, and, like
/// <see cref="LinkQuarantineProductCommand"/>, posts the entry ledger group.
/// </summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="LineNo">The line being identified.</param>
/// <param name="ProductId">The registered product.</param>
/// <param name="BatchId">The lot of the found goods, for batch-tracked products.</param>
/// <param name="Note">An optional identification note.</param>
public sealed record RegisterQuarantineProductCommand(
    QuarantineIncidentId IncidentId,
    int LineNo,
    ProductId ProductId,
    BatchId? BatchId = null,
    string? Note = null)
    : ICommand<QuarantineIncidentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Quarantine.Release;
}

/// <summary>
/// Releases part or all of an identified line into sellable Available stock,
/// posting the approved Quarantine → Available ledger group. Partial releases
/// are allowed; an incident resolves when its last unit is dispositioned.
/// </summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="LineNo">The line being released.</param>
/// <param name="Quantity">How much to release, capped at what remains.</param>
/// <param name="Note">An optional release note.</param>
public sealed record ReleaseQuarantineLineCommand(
    QuarantineIncidentId IncidentId,
    int LineNo,
    decimal Quantity,
    string? Note = null)
    : ICommand<QuarantineIncidentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Quarantine.Release;
}

/// <summary>
/// Rejects part or all of an identified line back to the supplier counterparty,
/// posting the approved Quarantine → External ledger group under the
/// <c>SupplierReturn</c> reason.
/// </summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="LineNo">The line being rejected.</param>
/// <param name="Quantity">How much to reject, capped at what remains.</param>
/// <param name="Note">An optional rejection note.</param>
public sealed record RejectQuarantineLineCommand(
    QuarantineIncidentId IncidentId,
    int LineNo,
    decimal Quantity,
    string? Note = null)
    : ICommand<QuarantineIncidentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Quarantine.Reject;
}

/// <summary>
/// Writes off part or all of an identified line to the EXT-WRITEOFF counterparty,
/// posting an approved Quarantine → External ledger group with a shrinkage
/// reason. The handler additionally requires <c>inventory.adjust.approve</c>.
/// </summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="LineNo">The line being written off.</param>
/// <param name="Quantity">How much to write off, capped at what remains.</param>
/// <param name="ReasonCode">The shrinkage reason.</param>
/// <param name="Note">An optional write-off note.</param>
public sealed record WriteOffQuarantineLineCommand(
    QuarantineIncidentId IncidentId,
    int LineNo,
    decimal Quantity,
    AdjustmentReasonCode ReasonCode,
    string? Note = null)
    : ICommand<QuarantineIncidentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Quarantine.Reject;
}