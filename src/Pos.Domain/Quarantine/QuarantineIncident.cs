using Pos.Domain.Common;

namespace Pos.Domain.Quarantine;

/// <summary>
/// The lifecycle of a quarantine incident: stock found that the system has not
/// authorized for sale (an unknown barcode, a known product outside the recorded
/// balance, or goods that should not be on site). Incidents start open, head
/// office investigates and identifies each line against a product, then every
/// line is dispositioned — released to Available, rejected back to the supplier,
/// or written off. The stock enters the Quarantine ledger bucket at
/// identification and each disposition moves it out again.
/// </summary>
public enum QuarantineIncidentStatus
{
    /// <summary>The incident was raised and awaits head office review.</summary>
    Open = 1,

    /// <summary>Head office is actively investigating the goods.</summary>
    UnderReview = 2,

    /// <summary>Every line has been dispositioned; no further action is possible.</summary>
    Resolved = 3,
}

/// <summary>How a quarantined line left the quarantine bucket.</summary>
public enum QuarantineDisposition
{
    /// <summary>The line has not been dispositioned yet.</summary>
    None = 0,

    /// <summary>The goods were released into sellable Available stock.</summary>
    Released = 1,

    /// <summary>The goods were rejected and returned to the supplier counterparty.</summary>
    Rejected = 2,

    /// <summary>The goods were written off as shrinkage.</summary>
    WrittenOff = 3,
}

/// <summary>One step in an incident's audit timeline.</summary>
public enum QuarantineEventKind
{
    /// <summary>The incident was raised.</summary>
    Raised = 1,

    /// <summary>A photograph was attached.</summary>
    PhotoAdded = 2,

    /// <summary>Head office recorded an investigation step.</summary>
    Investigation = 3,

    /// <summary>A line was linked to an existing product in the catalogue.</summary>
    ProductLinked = 4,

    /// <summary>A line was linked to a product registered for this incident.</summary>
    ProductRegistered = 5,

    /// <summary>Quantity was released to Available.</summary>
    Released = 6,

    /// <summary>Quantity was rejected back to the supplier.</summary>
    Rejected = 7,

    /// <summary>Quantity was written off.</summary>
    WrittenOff = 8,

    /// <summary>The incident completed when its last line was dispositioned.</summary>
    Resolved = 9,
}

/// <summary>One line of a quarantine incident, as submitted by the raising store.</summary>
/// <param name="Barcode">The barcode that was scanned or found on the goods.</param>
/// <param name="Quantity">The found quantity, in the product's base unit.</param>
/// <param name="UnitCost">The recorded value per unit, or null to fall back to the product default.</param>
/// <param name="ClaimedProductName">A best-effort description from the raising store, shown to head office.</param>
public sealed record QuarantineLineSpec(
    string Barcode,
    decimal Quantity,
    decimal? UnitCost = null,
    string? ClaimedProductName = null);

/// <summary>
/// The unauthorized-stock document. All state-changing operations run through
/// these domain methods; a state that cannot be reached by a domain transition
/// cannot be persisted.
/// </summary>
public sealed class QuarantineIncident
{
    /// <summary>The largest photograph an incident may carry.</summary>
    public const int MaxPhotoBytes = 5 * 1024 * 1024;

    private readonly List<QuarantineIncidentLine> _lines = [];
    private readonly List<QuarantinePhoto> _photos = [];
    private readonly List<QuarantineEvent> _timeline = [];

    private QuarantineIncident(
        QuarantineIncidentId id,
        DocumentNumber number,
        LocationId locationId,
        UserId createdByUserId,
        DateTimeOffset createdAtUtc,
        string? note)
    {
        Id = id;
        Number = number.Value;
        LocationId = locationId;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        Note = note;
        Status = QuarantineIncidentStatus.Open;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private QuarantineIncident()
    {
        Number = string.Empty;
        LocationId = LocationId.Empty;
        CreatedByUserId = UserId.Empty;
    }

    /// <summary>Gets the incident identifier.</summary>
    public QuarantineIncidentId Id { get; }

    /// <summary>Gets the human-readable QRT document number.</summary>
    public string Number { get; }

    /// <summary>Gets the current lifecycle status.</summary>
    public QuarantineIncidentStatus Status { get; private set; }

    /// <summary>Gets the location where the goods were found.</summary>
    public LocationId LocationId { get; }

    /// <summary>Gets the store user who raised the incident.</summary>
    public UserId CreatedByUserId { get; }

    /// <summary>Gets when the incident was raised.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets who dispositioned the incident's final line, when resolved.</summary>
    public UserId? ResolvedByUserId { get; private set; }

    /// <summary>Gets when the incident was resolved, when resolved.</summary>
    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    /// <summary>Gets when head office first moved the incident under review, when that happened.</summary>
    public DateTimeOffset? InvestigatedAtUtc { get; private set; }

    /// <summary>Gets the note captured when the incident was raised, if any.</summary>
    public string? Note { get; }

    /// <summary>Gets the quarantined lines, in order.</summary>
    public IReadOnlyList<QuarantineIncidentLine> Lines => _lines;

    /// <summary>Gets the photographs attached to the incident.</summary>
    public IReadOnlyList<QuarantinePhoto> Photos => _photos;

    /// <summary>Gets the audit timeline of the incident.</summary>
    public IReadOnlyList<QuarantineEvent> Timeline => _timeline;

    /// <summary>Gets the recorded value of every line at the declared cost.</summary>
    public decimal TotalValue => _lines.Sum(l => l.Quantity * l.UnitCost);

    /// <summary>Gets whether every awarded quantity of every line is dispositioned.</summary>
    public bool IsFullyDispositioned => _lines.Count > 0 && _lines.All(l => l.RemainingQuantity == 0m);

    /// <summary>
    /// Creates an incident. The QRT document number is allocated by the caller so
    /// it is never exposed to races on the counter.
    /// </summary>
    /// <param name="number">The allocated QRT number.</param>
    /// <param name="locationId">The location where the goods were found.</param>
    /// <param name="lines">The lines to quarantine.</param>
    /// <param name="createdByUserId">The user raising the incident.</param>
    /// <param name="createdAtUtc">The current instant.</param>
    /// <param name="note">The note captured at raise time, if any.</param>
    /// <returns>The incident, or validation errors.</returns>
    public static Result<QuarantineIncident> Create(
        DocumentNumber number,
        LocationId locationId,
        IReadOnlyList<QuarantineLineSpec> lines,
        UserId createdByUserId,
        DateTimeOffset createdAtUtc,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (locationId.IsEmpty)
        {
            return Result<QuarantineIncident>.Failure(QuarantineErrors.LocationRequired);
        }

        if (lines.Count == 0)
        {
            return Result<QuarantineIncident>.Failure(QuarantineErrors.EmptyIncident);
        }

        for (int i = 0; i < lines.Count; i++)
        {
            Result lineError = ValidateLine(lines[i], i + 1);

            if (lineError.IsFailure)
            {
                return Result<QuarantineIncident>.Failure(lineError.Errors);
            }
        }

        QuarantineIncident incident = new(
            QuarantineIncidentId.New(),
            number,
            locationId,
            createdByUserId,
            createdAtUtc,
            note);

        for (int i = 0; i < lines.Count; i++)
        {
            QuarantineLineSpec spec = lines[i];
            incident._lines.Add(new QuarantineIncidentLine(
                incident.Id,
                i + 1,
                spec.Barcode.Trim(),
                spec.Quantity,
                spec.UnitCost ?? 0m,
                spec.ClaimedProductName?.Trim()));
        }

        incident._timeline.Add(new QuarantineEvent(
            QuarantineEventId.New(),
            incident.Id,
            sequence: 1,
            QuarantineEventKind.Raised,
            createdByUserId,
            createdAtUtc,
            quantity: null,
            note));

        return Result<QuarantineIncident>.Success(incident);
    }

    /// <summary>
    /// Attaches a photograph taken at the scene. Uploaded bytes are stored with
    /// the incident so head office can judge the goods before dispositioning.
    /// </summary>
    /// <param name="photoId">The photo identifier.</param>
    /// <param name="fileName">The original file name.</param>
    /// <param name="contentType">The MIME type of the image.</param>
    /// <param name="data">The image bytes.</param>
    /// <param name="note">An optional caption.</param>
    /// <param name="uploadedByUserId">The user uploading.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a validation error.</returns>
    public Result AddPhoto(
        QuarantinePhotoId photoId,
        string? fileName,
        string? contentType,
        byte[] data,
        string? note,
        UserId uploadedByUserId,
        DateTimeOffset now)
    {
        if (Status == QuarantineIncidentStatus.Resolved)
        {
            return Result.Failure(QuarantineErrors.IncidentResolved);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Result.Failure(QuarantineErrors.PhotoFileNameRequired);
        }

        if (string.IsNullOrWhiteSpace(contentType))
        {
            return Result.Failure(QuarantineErrors.PhotoContentTypeRequired);
        }

        if (data.Length == 0)
        {
            return Result.Failure(QuarantineErrors.PhotoDataRequired);
        }

        if (data.Length > MaxPhotoBytes)
        {
            return Result.Failure(QuarantineErrors.PhotoTooLarge(MaxPhotoBytes));
        }

        _photos.Add(new QuarantinePhoto(
            photoId,
            Id,
            fileName!.Trim(),
            contentType!.Trim(),
            data,
            note,
            uploadedByUserId,
            now));

        AppendEvent(QuarantineEventKind.PhotoAdded, uploadedByUserId, now, null, note);
        return Result.Success();
    }

    /// <summary>Marks the incident as under active head office review.</summary>
    /// <param name="byUserId">The investigating user.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="note">What the investigation established, when provided.</param>
    /// <returns>Success, or a lifecycle error.</returns>
    public Result SetUnderReview(UserId byUserId, DateTimeOffset now, string? note)
    {
        if (Status == QuarantineIncidentStatus.Resolved)
        {
            return Result.Failure(QuarantineErrors.IncidentResolved);
        }

        Status = QuarantineIncidentStatus.UnderReview;
        InvestigatedAtUtc ??= now;
        AppendEvent(QuarantineEventKind.Investigation, byUserId, now, null, note);
        return Result.Success();
    }

    /// <summary>
    /// Identifies a line against a product: either an existing catalogue product
    /// whose barcode was scanned (linked), or a product registered for this
    /// incident (registered). Only the first identification is accepted; a
    /// duplicate call is a conflict, not an overwrite.
    /// </summary>
    /// <param name="lineNumber">The line being identified.</param>
    /// <param name="productId">The identified product.</param>
    /// <param name="batchId">The lot of the found goods, for batch-tracked products.</param>
    /// <param name="registered">Whether the product was registered for this incident.</param>
    /// <param name="byUserId">The head office user identifying the line.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="note">An optional identification note.</param>
    /// <returns>Success, or a lifecycle error.</returns>
    public Result Identify(
        int lineNumber,
        ProductId productId,
        BatchId? batchId,
        bool registered,
        UserId byUserId,
        DateTimeOffset now,
        string? note)
    {
        if (Status == QuarantineIncidentStatus.Resolved)
        {
            return Result.Failure(QuarantineErrors.IncidentResolved);
        }

        QuarantineIncidentLine? line = FindLine(lineNumber);

        if (line is null)
        {
            return Result.Failure(QuarantineErrors.LineUnknown(lineNumber));
        }

        if (line.ProductId is not null)
        {
            return Result.Failure(QuarantineErrors.LineAlreadyIdentified(lineNumber));
        }

        line.Identify(productId, batchId);

        AppendEvent(
            registered ? QuarantineEventKind.ProductRegistered : QuarantineEventKind.ProductLinked,
            byUserId,
            now,
            line.Quantity,
            note);

        return Result.Success();
    }

    /// <summary>
    /// Releases part or all of a line into sellable Available stock. The caller
    /// (the handler) posts the Quarantine → Available ledger group; this is the
    /// domain's authorization that the goods are in quarantine and the amount is
    /// within the recorded quantity.
    /// </summary>
    /// <param name="lineNumber">The line being released.</param>
    /// <param name="quantity">The quantity to release, capped at what remains.</param>
    /// <param name="byUserId">The approving head office user.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="note">An optional release note.</param>
    /// <returns>Success, or a lifecycle error.</returns>
    public Result Release(int lineNumber, decimal quantity, UserId byUserId, DateTimeOffset now, string? note)
        => Disposition(lineNumber, quantity, QuarantineDisposition.Released, byUserId, now, note);

    /// <summary>
    /// Rejects part or all of a line back to the supplier counterparty. The
    /// caller posts the Quarantine → External ledger group.
    /// </summary>
    /// <param name="lineNumber">The line being rejected.</param>
    /// <param name="quantity">The quantity to reject, capped at what remains.</param>
    /// <param name="byUserId">The approving head office user.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="note">An optional rejection note.</param>
    /// <returns>Success, or a lifecycle error.</returns>
    public Result Reject(int lineNumber, decimal quantity, UserId byUserId, DateTimeOffset now, string? note)
        => Disposition(lineNumber, quantity, QuarantineDisposition.Rejected, byUserId, now, note);

    /// <summary>
    /// Writes part or all of a line off as shrinkage. The caller posts the
    /// Quarantine → External (write-off) ledger group.
    /// </summary>
    /// <param name="lineNumber">The line being written off.</param>
    /// <param name="quantity">The quantity to write off, capped at what remains.</param>
    /// <param name="byUserId">The approving head office user.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="note">An optional write-off note.</param>
    /// <returns>Success, or a lifecycle error.</returns>
    public Result WriteOff(int lineNumber, decimal quantity, UserId byUserId, DateTimeOffset now, string? note)
        => Disposition(lineNumber, quantity, QuarantineDisposition.WrittenOff, byUserId, now, note);

    private Result Disposition(
        int lineNumber,
        decimal quantity,
        QuarantineDisposition disposition,
        UserId byUserId,
        DateTimeOffset now,
        string? note)
    {
        if (Status == QuarantineIncidentStatus.Resolved)
        {
            return Result.Failure(QuarantineErrors.IncidentResolved);
        }

        QuarantineIncidentLine? line = FindLine(lineNumber);

        if (line is null)
        {
            return Result.Failure(QuarantineErrors.LineUnknown(lineNumber));
        }

        if (line.ProductId is null)
        {
            return Result.Failure(QuarantineErrors.LineNotIdentified(lineNumber));
        }

        if (line.RemainingQuantity == 0m)
        {
            return Result.Failure(QuarantineErrors.LineAlreadyDispositioned(lineNumber));
        }

        if (quantity <= 0m || quantity > line.RemainingQuantity)
        {
            return Result.Failure(QuarantineErrors.QuantityExceedsLine(lineNumber, quantity, line.RemainingQuantity));
        }

        QuarantineEventKind kind = disposition switch
        {
            QuarantineDisposition.Released => QuarantineEventKind.Released,
            QuarantineDisposition.Rejected => QuarantineEventKind.Rejected,
            _ => QuarantineEventKind.WrittenOff,
        };

        line.ApplyDisposition(disposition, quantity, byUserId, now, note);
        AppendEvent(kind, byUserId, now, quantity, note);

        if (IsFullyDispositioned)
        {
            Status = QuarantineIncidentStatus.Resolved;
            ResolvedByUserId = byUserId;
            ResolvedAtUtc = now;
            AppendEvent(QuarantineEventKind.Resolved, byUserId, now, null, null);
        }

        return Result.Success();
    }

    private QuarantineIncidentLine? FindLine(int lineNumber)
        => _lines.FirstOrDefault(l => l.LineNo == lineNumber);

    private void AppendEvent(
        QuarantineEventKind kind,
        UserId actorUserId,
        DateTimeOffset occurredAtUtc,
        decimal? quantity,
        string? note)
        => _timeline.Add(new QuarantineEvent(
            QuarantineEventId.New(),
            Id,
            _timeline.Count + 1,
            kind,
            actorUserId,
            occurredAtUtc,
            quantity,
            note));

    private static Result ValidateLine(QuarantineLineSpec spec, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(spec.Barcode))
        {
            return Result.Failure(QuarantineErrors.BarcodeRequired(lineNumber));
        }

        if (spec.Quantity <= 0m)
        {
            return Result.Failure(QuarantineErrors.LineQuantityInvalid(lineNumber));
        }

        if (spec.UnitCost is < 0m)
        {
            return Result.Failure(QuarantineErrors.LineCostInvalid(lineNumber));
        }

        return Result.Success();
    }
}

/// <summary>One quarantined line of an incident.</summary>
public sealed class QuarantineIncidentLine
{
    internal QuarantineIncidentLine(
        QuarantineIncidentId incidentId,
        int lineNumber,
        string barcode,
        decimal quantity,
        decimal unitCost,
        string? claimedProductName)
    {
        Id = QuarantineLineId.New();
        IncidentId = incidentId;
        LineNo = lineNumber;
        Barcode = barcode;
        Quantity = quantity;
        UnitCost = unitCost;
        ClaimedProductName = claimedProductName;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private QuarantineIncidentLine()
    {
        Barcode = string.Empty;
    }

    /// <summary>Gets the line identifier.</summary>
    public QuarantineLineId Id { get; }

    /// <summary>Gets the owning incident.</summary>
    public QuarantineIncidentId IncidentId { get; }

    /// <summary>Gets the line's ordinal within the incident.</summary>
    public int LineNo { get; }

    /// <summary>Gets the barcode that was scanned or found.</summary>
    public string Barcode { get; }

    /// <summary>Gets the found quantity, in the product's base unit.</summary>
    public decimal Quantity { get; }

    /// <summary>Gets the recorded value per unit.</summary>
    public decimal UnitCost { get; }

    /// <summary>Gets the raising store's best-effort description, if any.</summary>
    public string? ClaimedProductName { get; }

    /// <summary>Gets the product identified for this line, when identified.</summary>
    public ProductId? ProductId { get; private set; }

    /// <summary>Gets the lot of the found goods, when named.</summary>
    public BatchId? BatchId { get; private set; }

    /// <summary>Gets how much of the line has been dispositioned so far.</summary>
    public decimal DispositionedQuantity { get; private set; }

    /// <summary>Gets how much of the line remains to be dispositioned.</summary>
    public decimal RemainingQuantity => Quantity - DispositionedQuantity;

    /// <summary>Gets the latest disposition applied to the line, if any.</summary>
    public QuarantineDisposition Disposition { get; private set; } = QuarantineDisposition.None;

    /// <summary>Gets who applied the latest disposition.</summary>
    public UserId? DispositionedByUserId { get; private set; }

    /// <summary>Gets when the latest disposition was applied.</summary>
    public DateTimeOffset? DispositionedAtUtc { get; private set; }

    /// <summary>Gets the note carried by the latest disposition.</summary>
    public string? DispositionNote { get; private set; }

    internal void Identify(ProductId productId, BatchId? batchId)
    {
        ProductId = productId;
        BatchId = batchId;
    }

    internal void ApplyDisposition(
        QuarantineDisposition disposition,
        decimal quantity,
        UserId byUserId,
        DateTimeOffset now,
        string? note)
    {
        DispositionedQuantity += quantity;
        Disposition = disposition;
        DispositionedByUserId = byUserId;
        DispositionedAtUtc = now;
        DispositionNote = note;
    }
}

/// <summary>A photograph taken at the scene and attached to an incident.</summary>
public sealed class QuarantinePhoto
{
    internal QuarantinePhoto(
        QuarantinePhotoId id,
        QuarantineIncidentId incidentId,
        string fileName,
        string contentType,
        byte[] data,
        string? note,
        UserId uploadedByUserId,
        DateTimeOffset uploadedAtUtc)
    {
        Id = id;
        IncidentId = incidentId;
        FileName = fileName;
        ContentType = contentType;
        Data = data;
        Note = note;
        UploadedByUserId = uploadedByUserId;
        UploadedAtUtc = uploadedAtUtc;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private QuarantinePhoto()
    {
        FileName = string.Empty;
        ContentType = string.Empty;
        Data = [];
    }

    /// <summary>Gets the photo identifier.</summary>
    public QuarantinePhotoId Id { get; }

    /// <summary>Gets the owning incident.</summary>
    public QuarantineIncidentId IncidentId { get; }

    /// <summary>Gets the original file name.</summary>
    public string FileName { get; }

    /// <summary>Gets the image MIME type.</summary>
    public string ContentType { get; }

    /// <summary>Gets the image bytes.</summary>
    public byte[] Data { get; }

    /// <summary>Gets an optional caption.</summary>
    public string? Note { get; }

    /// <summary>Gets who uploaded the photo.</summary>
    public UserId UploadedByUserId { get; }

    /// <summary>Gets when the photo was uploaded.</summary>
    public DateTimeOffset UploadedAtUtc { get; }
}

/// <summary>One step in an incident's audit timeline.</summary>
public sealed class QuarantineEvent
{
    internal QuarantineEvent(
        QuarantineEventId id,
        QuarantineIncidentId incidentId,
        int sequence,
        QuarantineEventKind kind,
        UserId actorUserId,
        DateTimeOffset occurredAtUtc,
        decimal? quantity,
        string? note)
    {
        Id = id;
        IncidentId = incidentId;
        Sequence = sequence;
        Kind = kind;
        ActorUserId = actorUserId;
        OccurredAtUtc = occurredAtUtc;
        Quantity = quantity;
        Note = note;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private QuarantineEvent()
    {
    }

    /// <summary>Gets the event identifier.</summary>
    public QuarantineEventId Id { get; }

    /// <summary>Gets the owning incident.</summary>
    public QuarantineIncidentId IncidentId { get; }

    /// <summary>Gets the event's ordinal within the timeline.</summary>
    public int Sequence { get; }

    /// <summary>Gets the event kind.</summary>
    public QuarantineEventKind Kind { get; }

    /// <summary>Gets who caused the event.</summary>
    public UserId ActorUserId { get; }

    /// <summary>Gets when the event happened.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Gets the quantity a disposition moved, where the event is a disposition.</summary>
    public decimal? Quantity { get; }

    /// <summary>Gets the free-text note, if any.</summary>
    public string? Note { get; }
}