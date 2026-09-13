using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Catalog;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Quarantine;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Quarantine;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>The body of a quarantine incident creation.</summary>
/// <param name="LocationId">The location where the goods were found.</param>
/// <param name="Lines">The lines to quarantine, in the products' base units.</param>
/// <param name="Note">An optional note explaining the finding.</param>
public sealed record CreateQuarantineIncidentBody(
    Guid LocationId,
    IReadOnlyList<CreateQuarantineLineBody> Lines,
    string? Note = null);

/// <summary>One line of a quarantine incident creation.</summary>
/// <param name="Barcode">The barcode that was scanned or found on the goods.</param>
/// <param name="Quantity">The found quantity, in the product's base unit.</param>
/// <param name="UnitCost">The recorded value per unit, or null to fall back to the product default.</param>
/// <param name="ClaimedProductName">A best-effort description from the raising store.</param>
public sealed record CreateQuarantineLineBody(
    string Barcode,
    decimal Quantity,
    decimal? UnitCost = null,
    string? ClaimedProductName = null);

/// <summary>The body of a photograph attachment.</summary>
/// <param name="FileName">The original file name.</param>
/// <param name="ContentType">The image MIME type.</param>
/// <param name="Data">The image bytes, base64-encoded.</param>
/// <param name="Note">An optional caption.</param>
public sealed record AddQuarantinePhotoBody(
    string FileName,
    string ContentType,
    string Data,
    string? Note = null);

/// <summary>The body of a product link.</summary>
/// <param name="LineNo">The line being identified.</param>
/// <param name="ProductId">The catalogue product the goods were identified as.</param>
/// <param name="BatchId">The lot of the found goods, for batch-tracked products.</param>
/// <param name="Note">An optional identification note.</param>
public sealed record LinkQuarantineProductBody(
    int LineNo,
    Guid ProductId,
    Guid? BatchId = null,
    string? Note = null);

/// <summary>
/// The body of an on-the-spot product registration. The product master is
/// created carrying the scanned barcode, then the line is identified against it.
/// Whether the product tracks batches is derived from whether a lot is named.
/// </summary>
/// <param name="LineNo">The line being identified.</param>
/// <param name="Sku">The stock-keeping unit code.</param>
/// <param name="Name">The display name.</param>
/// <param name="CategoryId">The category.</param>
/// <param name="BaseUnitOfMeasureId">The unit the ledger counts in.</param>
/// <param name="Description">Free-text description, or null.</param>
/// <param name="BrandId">The brand, or null.</param>
/// <param name="PrimarySupplierId">The primary supplier, or null.</param>
/// <param name="TaxCode">The VAT/tax code, or null.</param>
/// <param name="IsVatExempt">Whether the product is VAT-exempt.</param>
/// <param name="DefaultPurchaseCost">The default purchase cost per base unit.</param>
/// <param name="BatchId">The lot of the found goods, when the goods are batch-tracked.</param>
/// <param name="Note">An optional identification note.</param>
public sealed record RegisterQuarantineProductBody(
    int LineNo,
    string Sku,
    string Name,
    Guid CategoryId,
    Guid BaseUnitOfMeasureId,
    string? Description = null,
    Guid? BrandId = null,
    Guid? PrimarySupplierId = null,
    string? TaxCode = null,
    bool IsVatExempt = false,
    decimal DefaultPurchaseCost = 0m,
    Guid? BatchId = null,
    string? Note = null);

/// <summary>The body of a quantity disposition.</summary>
/// <param name="LineNo">The line being dispositioned.</param>
/// <param name="Quantity">How many units, capped at what remains.</param>
/// <param name="Note">An optional disposition note.</param>
public sealed record QuarantineQuantityBody(int LineNo, decimal Quantity, string? Note = null);

/// <summary>The body of a write-off disposition.</summary>
/// <param name="LineNo">The line being written off.</param>
/// <param name="Quantity">How many units, capped at what remains.</param>
/// <param name="ReasonCode">The shrinkage reason code.</param>
/// <param name="Note">An optional write-off note.</param>
public sealed record WriteOffQuarantineLineBody(
    int LineNo,
    decimal Quantity,
    AdjustmentReasonCode ReasonCode,
    string? Note = null);

/// <summary>A quarantine incident as it appears in a list.</summary>
public sealed record QuarantineIncidentSummary(
    Guid Id,
    string Number,
    string Status,
    Guid LocationId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? InvestigatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    decimal TotalValue,
    int LineCount,
    int OpenLineCount);

/// <summary>One line of a quarantine incident detail.</summary>
public sealed record QuarantineLineSummary(
    int LineNo,
    string Barcode,
    decimal Quantity,
    decimal UnitCost,
    decimal RemainingQuantity,
    string? ClaimedProductName,
    Guid? ProductId,
    Guid? BatchId,
    string Disposition,
    Guid? DispositionedByUserId,
    DateTimeOffset? DispositionedAtUtc,
    string? DispositionNote);

/// <summary>One photograph of an incident, without the image bytes.</summary>
public sealed record QuarantinePhotoSummary(
    Guid Id,
    string FileName,
    string ContentType,
    string? Note,
    Guid UploadedByUserId,
    DateTimeOffset UploadedAtUtc);

/// <summary>One step in an incident's audit timeline.</summary>
public sealed record QuarantineEventSummary(
    int Sequence,
    string Kind,
    Guid ActorUserId,
    DateTimeOffset OccurredAtUtc,
    decimal? Quantity,
    string? Note);

/// <summary>A quarantine incident as returned by the detail route.</summary>
public sealed record QuarantineIncidentDetail(
    Guid Id,
    string Number,
    string Status,
    Guid LocationId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? InvestigatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    string? Note,
    decimal TotalValue,
    IReadOnlyList<QuarantineLineSummary> Lines,
    IReadOnlyList<QuarantinePhotoSummary> Photos,
    IReadOnlyList<QuarantineEventSummary> Timeline);

/// <summary>Quarantine incident endpoints.</summary>
public static class QuarantineEndpoints
{
    /// <summary>How large a scene photo may be, matching the aggregate's limit.</summary>
    public const int MaxPhotoBytes = QuarantineIncident.MaxPhotoBytes;

    /// <summary>Maps the quarantine routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapQuarantineEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/quarantine").WithTags("Quarantine");

        group.MapGet("/", ListQuarantineIncidentsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListQuarantineIncidents")
            .WithSummary("Lists quarantine incidents within the caller's scope.");

        group.MapPost("/", CreateQuarantineIncidentAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.Create)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateQuarantineIncident")
            .WithSummary("Raises a quarantine incident for unauthorized stock.");

        group.MapGet("/{id:guid}", GetQuarantineIncidentAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetQuarantineIncident")
            .WithSummary("Gets a quarantine incident with its lines, photos and timeline.");

        group.MapGet("/{id:guid}/photos/{photoId:guid}", GetQuarantinePhotoAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetQuarantinePhoto")
            .WithSummary("Downloads a scene photograph.");

        group.MapPost("/{id:guid}/investigate", InvestigateQuarantineIncidentAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.Investigate)
            {
                Scope = ScopeSource.None,
            })
            .WithName("InvestigateQuarantineIncident")
            .WithSummary("Marks an incident as under active head office review.");

        group.MapPost("/{id:guid}/photos", AddQuarantinePhotoAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.Create)
            {
                Scope = ScopeSource.None,
            })
            .WithName("AddQuarantinePhoto")
            .WithSummary("Attaches a scene photograph to an incident.");

        group.MapPost("/{id:guid}/link-product", LinkQuarantineProductAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.Release)
            {
                Scope = ScopeSource.None,
            })
            .WithName("LinkQuarantineProduct")
            .WithSummary("Identifies a line against an existing product and posts the entry ledger.");

        group.MapPost("/{id:guid}/register-product", RegisterQuarantineProductAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.Release)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RegisterQuarantineProduct")
            .WithSummary("Creates a product for the scanned barcode and identifies the line against it.");

        group.MapPost("/{id:guid}/release", ReleaseQuarantineLineAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.Release)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ReleaseQuarantineLine")
            .WithSummary("Releases quantity into sellable Available stock.");

        group.MapPost("/{id:guid}/reject", RejectQuarantineLineAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.Reject)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RejectQuarantineLine")
            .WithSummary("Rejects quantity back to the supplier counterparty.");

        group.MapPost("/{id:guid}/write-off", WriteOffQuarantineLineAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Quarantine.Reject)
            {
                Scope = ScopeSource.None,
            })
            .WithName("WriteOffQuarantineLine")
            .WithSummary("Writes off quantity as shrinkage; additionally requires inventory.adjust.approve.");

        return app;
    }

    private static async Task<IResult> ListQuarantineIncidentsAsync(
        [FromServices] PosDbContext context,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        IQueryable<QuarantineIncident> query = context.QuarantineIncidents.AsNoTracking();

        // The list is scoped to the caller's assigned locations unless they act
        // business-wide. Permission authority is resolved from the database, not
        // the token, so a store manager sees every store they are assigned to.
        if (!authorization.HasAllLocations)
        {
            query = query.Where(q => authorization.Locations.Contains(q.LocationId));
        }

        List<QuarantineIncidentSummary> summaries = await query
            .OrderByDescending(q => q.CreatedAtUtc)
            .Select(q => new QuarantineIncidentSummary(
                q.Id.Value,
                q.Number,
                q.Status.ToString(),
                q.LocationId.Value,
                q.CreatedByUserId.Value,
                q.CreatedAtUtc,
                q.InvestigatedAtUtc,
                q.ResolvedAtUtc,
                q.TotalValue,
                q.Lines.Count,
                q.Lines.Count(l => l.Quantity - l.DispositionedQuantity > 0m)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(summaries);
    }

    private static async Task<IResult> CreateQuarantineIncidentAsync(
        [FromBody] CreateQuarantineIncidentBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<QuarantineIncidentId> result = await dispatcher
            .SendAsync(
                new CreateQuarantineIncidentCommand(
                    new LocationId(body.LocationId),
                    [.. body.Lines.Select(l => new QuarantineLineSpec(
                        l.Barcode,
                        l.Quantity,
                        l.UnitCost,
                        l.ClaimedProductName))],
                    body.Note),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/quarantine/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> GetQuarantineIncidentAsync(
        Guid id,
        [FromServices] PosDbContext context,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        QuarantineIncidentId incidentId = new(id);

        QuarantineIncident? incident = await context.QuarantineIncidents
            .AsNoTracking()
            .Include(q => q.Lines)
            .Include(q => q.Photos)
            .Include(q => q.Timeline)
            .FirstOrDefaultAsync(q => q.Id == incidentId, cancellationToken)
            .ConfigureAwait(false);

        if (incident is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<QuarantineIncidentDetail>.Failure(QuarantineErrors.IncidentUnknown(incidentId)),
                currentUser.CorrelationId.Value);
        }

        if (!await CanViewAsync(incident, evaluator, currentUser, cancellationToken).ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<QuarantineIncidentDetail>.Failure(QuarantineErrors.IncidentOutsideScope(incidentId)),
                currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(ToDetail(incident));
    }

    private static async Task<IResult> GetQuarantinePhotoAsync(
        Guid id,
        Guid photoId,
        [FromServices] PosDbContext context,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        QuarantineIncidentId incidentId = new(id);

        QuarantineIncident? incident = await context.QuarantineIncidents
            .AsNoTracking()
            .Include(q => q.Photos)
            .FirstOrDefaultAsync(q => q.Id == incidentId, cancellationToken)
            .ConfigureAwait(false);

        if (incident is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<byte[]>.Failure(QuarantineErrors.IncidentUnknown(incidentId)),
                currentUser.CorrelationId.Value);
        }

        if (!await CanViewAsync(incident, evaluator, currentUser, cancellationToken).ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<byte[]>.Failure(QuarantineErrors.IncidentOutsideScope(incidentId)),
                currentUser.CorrelationId.Value);
        }

        QuarantinePhoto? photo = incident.Photos.FirstOrDefault(p => p.Id.Value == photoId);

        return photo is null
            ? ProblemDetailsMapping.ToProblem(
                Result<byte[]>.Failure(QuarantineErrors.PhotoUnknown(new QuarantinePhotoId(photoId))),
                currentUser.CorrelationId.Value)
            : TypedResults.File(photo.Data, photo.ContentType, photo.FileName);
    }

    private static async Task<IResult> InvestigateQuarantineIncidentAsync(
        Guid id,
        [FromBody] TransferReviewBody? body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new InvestigateQuarantineIncidentCommand(new QuarantineIncidentId(id), body?.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> AddQuarantinePhotoAsync(
        Guid id,
        [FromBody] AddQuarantinePhotoBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        // Decode before dispatch so an oversized or malformed payload never
        // reaches the aggregate; the cost of decoding is bounded by the check.
        if (body.Data.Length > (MaxPhotoBytes * 4 / 3) + 4)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<QuarantineIncidentId>.Failure(QuarantineErrors.PhotoTooLarge(MaxPhotoBytes)),
                currentUser.CorrelationId.Value);
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(body.Data);
        }
        catch (FormatException)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<QuarantineIncidentId>.Failure(QuarantineErrors.PhotoDataInvalid),
                currentUser.CorrelationId.Value);
        }

        if (data.Length > MaxPhotoBytes)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<QuarantineIncidentId>.Failure(QuarantineErrors.PhotoTooLarge(MaxPhotoBytes)),
                currentUser.CorrelationId.Value);
        }

        return await DispatchAsync(
            new AddQuarantinePhotoCommand(
                new QuarantineIncidentId(id),
                body.FileName,
                body.ContentType,
                data,
                body.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> LinkQuarantineProductAsync(
        Guid id,
        [FromBody] LinkQuarantineProductBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new LinkQuarantineProductCommand(
                new QuarantineIncidentId(id),
                body.LineNo,
                new ProductId(body.ProductId),
                body.BatchId is { } batchId ? new BatchId(batchId) : null,
                body.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> RegisterQuarantineProductAsync(
        Guid id,
        [FromBody] RegisterQuarantineProductBody body,
        [FromServices] PosDbContext context,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        QuarantineIncidentId incidentId = new(id);

        QuarantineIncidentLine? line = await context.QuarantineIncidentLines
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.IncidentId == incidentId && l.LineNo == body.LineNo, cancellationToken)
            .ConfigureAwait(false);

        if (line is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentUnknown(incidentId)),
                currentUser.CorrelationId.Value);
        }

        // Identify before creating a product master: a line that already has a
        // product must never cause an orphan registration.
        if (line.ProductId is not null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<QuarantineIncidentId>.Failure(QuarantineErrors.LineAlreadyIdentified(body.LineNo)),
                currentUser.CorrelationId.Value);
        }

        Result<ProductId> created = await dispatcher
            .SendAsync(
                new CreateProductCommand(
                    body.Sku,
                    body.Name,
                    new CategoryId(body.CategoryId),
                    new UnitOfMeasureId(body.BaseUnitOfMeasureId),
                    body.Description,
                    body.BrandId is { } brandId ? new BrandId(brandId) : null,
                    body.PrimarySupplierId is { } supplierId ? new SupplierId(supplierId) : null,
                    body.TaxCode,
                    body.IsVatExempt,
                    body.DefaultPurchaseCost,
                    body.BatchId is not null,
                    TracksExpiry: false,
                    ShelfLifeDays: null,
                    line.Barcode),
                cancellationToken)
            .ConfigureAwait(false);

        if (created.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(created, currentUser.CorrelationId.Value);
        }

        return await DispatchAsync(
            new RegisterQuarantineProductCommand(
                incidentId,
                body.LineNo,
                created.Value,
                body.BatchId is { } batchId ? new BatchId(batchId) : null,
                body.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ReleaseQuarantineLineAsync(
        Guid id,
        [FromBody] QuarantineQuantityBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new ReleaseQuarantineLineCommand(
                new QuarantineIncidentId(id),
                body.LineNo,
                body.Quantity,
                body.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> RejectQuarantineLineAsync(
        Guid id,
        [FromBody] QuarantineQuantityBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new RejectQuarantineLineCommand(
                new QuarantineIncidentId(id),
                body.LineNo,
                body.Quantity,
                body.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> WriteOffQuarantineLineAsync(
        Guid id,
        [FromBody] WriteOffQuarantineLineBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new WriteOffQuarantineLineCommand(
                new QuarantineIncidentId(id),
                body.LineNo,
                body.Quantity,
                body.ReasonCode,
                body.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> DispatchAsync(
        ICommand<QuarantineIncidentId> command,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<QuarantineIncidentId> result = await dispatcher
            .SendAsync(command, cancellationToken)
            .ConfigureAwait(false);

        return Complete(result, currentUser);
    }

    private static IResult Complete(Result<QuarantineIncidentId> result, ICurrentUser currentUser)
        => result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);

    private static async Task<bool> CanViewAsync(
        QuarantineIncident incident,
        IPermissionEvaluator evaluator,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await evaluator
            .HasPermissionAsync(
                currentUser.UserId ?? UserId.Empty,
                Permissions.Quarantine.View,
                incident.LocationId,
                cancellationToken)
            .ConfigureAwait(false);

    private static QuarantineIncidentDetail ToDetail(QuarantineIncident incident)
        => new(
            incident.Id.Value,
            incident.Number,
            incident.Status.ToString(),
            incident.LocationId.Value,
            incident.CreatedByUserId.Value,
            incident.CreatedAtUtc,
            incident.InvestigatedAtUtc,
            incident.ResolvedAtUtc,
            incident.Note,
            incident.TotalValue,
            [.. incident.Lines.OrderBy(l => l.LineNo).Select(l => new QuarantineLineSummary(
                l.LineNo,
                l.Barcode,
                l.Quantity,
                l.UnitCost,
                l.RemainingQuantity,
                l.ClaimedProductName,
                l.ProductId?.Value,
                l.BatchId?.Value,
                l.Disposition.ToString(),
                l.DispositionedByUserId?.Value,
                l.DispositionedAtUtc,
                l.DispositionNote))],
            [.. incident.Photos.Select(p => new QuarantinePhotoSummary(
                p.Id.Value,
                p.FileName,
                p.ContentType,
                p.Note,
                p.UploadedByUserId.Value,
                p.UploadedAtUtc))],
            [.. incident.Timeline.OrderBy(t => t.Sequence).Select(t => new QuarantineEventSummary(
                t.Sequence,
                t.Kind.ToString(),
                t.ActorUserId.Value,
                t.OccurredAtUtc,
                t.Quantity,
                t.Note))]);
}