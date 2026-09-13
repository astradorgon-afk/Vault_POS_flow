using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Quarantine;

namespace Pos.Application.Quarantine;

/// <summary>
/// Handles <see cref="CreateQuarantineIncidentCommand"/>. Allocates the QRT
/// number, resolves the scanned barcodes against the catalogue and identifies
/// every line whose product is already known (unless a batch is still required),
/// then posts one QuarantineEntry ledger group for those lines. Lines with
/// unknown barcodes stay unidentified for registration or linking by head office.
/// </summary>
public sealed class CreateQuarantineIncidentCommandHandler(
    IQuarantineRepository quarantine,
    IDocumentNumberGenerator numbers,
    IInventoryLedger ledger,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CreateQuarantineIncidentCommand, QuarantineIncidentId>
{
    /// <inheritdoc />
    public async Task<Result<QuarantineIncidentId>> HandleAsync(
        CreateQuarantineIncidentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        UserId actor = currentUser.UserId ?? UserId.Empty;
        DateTimeOffset now = clock.UtcNow;

        QuarantineLocationInfo? locationInfo = await quarantine
            .GetLocationInfoAsync(command.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (locationInfo is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.LocationUnknown(command.LocationId));
        }

        if (locationInfo.Kind == LocationKind.External)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentLocationExternal);
        }

        if (locationInfo.TimeZoneId is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.TimeZoneMissing(command.LocationId));
        }

        IReadOnlyDictionary<string, Product> productByBarcode = await quarantine
            .GetProductsByBarcodesAsync(
                [.. command.Lines.Select(l => l.Barcode.Trim()).Distinct()],
                cancellationToken)
            .ConfigureAwait(false);

        // A declared cost wins; unknown products keep the declared cost and fall
        // back to the product default when they are identified later.
        List<QuarantineLineSpec> specs = [.. command.Lines.Select(line => line with
        {
            UnitCost = line.UnitCost
                ?? productByBarcode.GetValueOrDefault(line.Barcode.Trim())?.DefaultPurchaseCost
                ?? 0m,
        })];

        DocumentNumber number = await numbers
            .NextAsync(DocumentType.QuarantineIncident, cancellationToken)
            .ConfigureAwait(false);

        Result<QuarantineIncident> created = QuarantineIncident.Create(
            number,
            command.LocationId,
            specs,
            actor,
            now,
            command.Note);

        if (created.IsFailure)
        {
            return Result<QuarantineIncidentId>.Failure(created.Errors);
        }

        QuarantineIncident incident = created.Value;

        // Lines whose barcode already resolves to a product that does not track
        // batches are identified immediately and enter quarantine now. Batch
        // tracked goods cannot move until the lot is named, so their entry is
        // deferred to the identification step.
        foreach (QuarantineIncidentLine line in incident.Lines)
        {
            if (!productByBarcode.TryGetValue(line.Barcode, out Product? product)
                || product.TracksBatches)
            {
                continue;
            }

            Result identified = incident.Identify(
                line.LineNo,
                product.Id,
                batchId: null,
                registered: false,
                actor,
                now,
                command.Note);

            if (identified.IsFailure)
            {
                return Result<QuarantineIncidentId>.Failure(identified.Errors);
            }
        }

        Result<QuarantineIncidentId> added = await quarantine
            .AddAsync(incident, cancellationToken)
            .ConfigureAwait(false);

        if (added.IsFailure)
        {
            return added;
        }

        (QuarantineIncidentLine Line, Product Product)[] entering =
            [.. incident.Lines
                .Where(l => l.ProductId is not null)
                .Select(l => (Line: l, Product: productByBarcode[l.Barcode]))];

        if (entering.Length == 0)
        {
            return Result<QuarantineIncidentId>.Success(incident.Id);
        }

        return await QuarantineIdentityPoster.PostEntryAsync(
            quarantine,
            ledger,
            incident,
            entering,
            actor,
            currentUser.DeviceId,
            currentUser.CorrelationId,
            locationInfo.Kind,
            now,
            clock.BusinessDateFor(locationInfo.TimeZoneId),
            command.Note,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Handles <see cref="AddQuarantinePhotoCommand"/>. Documents the found goods
/// with a scene photograph; the photograph becomes part of the incident's audit
/// record. The message is not location-scoped, so the handler re-checks
/// <c>quarantine.create</c> at the incident's location itself.
/// </summary>
public sealed class AddQuarantinePhotoCommandHandler(
    IQuarantineRepository quarantine,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<AddQuarantinePhotoCommand, QuarantineIncidentId>
{
    /// <inheritdoc />
    public async Task<Result<QuarantineIncidentId>> HandleAsync(
        AddQuarantinePhotoCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        UserId actor = currentUser.UserId ?? UserId.Empty;

        QuarantineIncident? incident = await quarantine
            .GetByIdAsync(command.IncidentId, cancellationToken)
            .ConfigureAwait(false);

        if (incident is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentUnknown(command.IncidentId));
        }

        if (!await permissions
                .HasPermissionAsync(actor, Permissions.Quarantine.Create, incident.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentOutsideScope(incident.Id));
        }

        Result added = incident.AddPhoto(
            QuarantinePhotoId.New(),
            command.FileName,
            command.ContentType,
            command.Data,
            command.Note,
            actor,
            clock.UtcNow);

        return added.IsSuccess
            ? Result<QuarantineIncidentId>.Success(incident.Id)
            : Result<QuarantineIncidentId>.Failure(added.Errors);
    }
}

/// <summary>
/// Handles <see cref="InvestigateQuarantineIncidentCommand"/>. Moves an open
/// incident to Under Review and records the investigation step on its timeline.
/// Purely a lifecycle transition; the ledger is untouched.
/// </summary>
public sealed class InvestigateQuarantineIncidentCommandHandler(
    IQuarantineRepository quarantine,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<InvestigateQuarantineIncidentCommand, QuarantineIncidentId>
{
    /// <inheritdoc />
    public async Task<Result<QuarantineIncidentId>> HandleAsync(
        InvestigateQuarantineIncidentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        UserId actor = currentUser.UserId ?? UserId.Empty;

        QuarantineIncident? incident = await quarantine
            .GetByIdAsync(command.IncidentId, cancellationToken)
            .ConfigureAwait(false);

        if (incident is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentUnknown(command.IncidentId));
        }

        if (!await permissions
                .HasPermissionAsync(actor, Permissions.Quarantine.Investigate, incident.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentOutsideScope(incident.Id));
        }

        Result investigated = incident.SetUnderReview(actor, clock.UtcNow, command.Note);

        return investigated.IsSuccess
            ? Result<QuarantineIncidentId>.Success(incident.Id)
            : Result<QuarantineIncidentId>.Failure(investigated.Errors);
    }
}

/// <summary>
/// Handles <see cref="LinkQuarantineProductCommand"/>. Identifies a line against
/// an existing catalogue product whose barcode already matches the scanned code,
/// then posts the QuarantineEntry group moving the goods from the supplier
/// counterparty into quarantine.
/// </summary>
public sealed class LinkQuarantineProductCommandHandler(
    IQuarantineRepository quarantine,
    IInventoryLedger ledger,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<LinkQuarantineProductCommand, QuarantineIncidentId>
{
    /// <inheritdoc />
    public async Task<Result<QuarantineIncidentId>> HandleAsync(
        LinkQuarantineProductCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        UserId actor = currentUser.UserId ?? UserId.Empty;

        Result<QuarantineIncident> loaded = await QuarantineIdentityPoster
            .LoadAsync(quarantine, command.IncidentId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<QuarantineIncidentId>.Failure(loaded.Errors);
        }

        QuarantineIncident incident = loaded.Value;

        if (!await permissions
                .HasPermissionAsync(actor, Permissions.Quarantine.Release, incident.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentOutsideScope(incident.Id));
        }

        return await QuarantineIdentityPoster.IdentifyAndEnterAsync(
            quarantine,
            ledger,
            incident,
            command.LineNo,
            command.ProductId,
            registered: false,
            command.BatchId,
            command.Note,
            actor,
            currentUser,
            clock,
            requireBarcodeOwnership: true,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Handles <see cref="RegisterQuarantineProductCommand"/>. Identifies a line
/// against a product the endpoint registered for this incident (which already
/// carries the scanned barcode) and posts the entry ledger group. Unlike the
/// link command there is no ownership re-check: the endpoint created the product
/// with the barcode moments ago.
/// </summary>
public sealed class RegisterQuarantineProductCommandHandler(
    IQuarantineRepository quarantine,
    IInventoryLedger ledger,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RegisterQuarantineProductCommand, QuarantineIncidentId>
{
    /// <inheritdoc />
    public async Task<Result<QuarantineIncidentId>> HandleAsync(
        RegisterQuarantineProductCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        UserId actor = currentUser.UserId ?? UserId.Empty;

        Result<QuarantineIncident> loaded = await QuarantineIdentityPoster
            .LoadAsync(quarantine, command.IncidentId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<QuarantineIncidentId>.Failure(loaded.Errors);
        }

        QuarantineIncident incident = loaded.Value;

        if (!await permissions
                .HasPermissionAsync(actor, Permissions.Quarantine.Release, incident.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentOutsideScope(incident.Id));
        }

        return await QuarantineIdentityPoster.IdentifyAndEnterAsync(
            quarantine,
            ledger,
            incident,
            command.LineNo,
            command.ProductId,
            registered: true,
            command.BatchId,
            command.Note,
            actor,
            currentUser,
            clock,
            requireBarcodeOwnership: false,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Handles <see cref="ReleaseQuarantineLineCommand"/>.</summary>
public sealed class ReleaseQuarantineLineCommandHandler(
    IQuarantineRepository quarantine,
    IInventoryLedger ledger,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ReleaseQuarantineLineCommand, QuarantineIncidentId>
{
    /// <inheritdoc />
    public Task<Result<QuarantineIncidentId>> HandleAsync(
        ReleaseQuarantineLineCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return new QuarantineLineDisposition(quarantine, ledger, permissions, currentUser, clock).ApplyAsync(
            incidentId: command.IncidentId,
            lineNo: command.LineNo,
            quantity: command.Quantity,
            kind: QuarantineDisposition.Released,
            reasonCode: null,
            note: command.Note,
            requiredPermission: Permissions.Quarantine.Release,
            additionalPermission: null,
            cancellationToken);
    }
}

/// <summary>Handles <see cref="RejectQuarantineLineCommand"/>.</summary>
public sealed class RejectQuarantineLineCommandHandler(
    IQuarantineRepository quarantine,
    IInventoryLedger ledger,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RejectQuarantineLineCommand, QuarantineIncidentId>
{
    /// <inheritdoc />
    public Task<Result<QuarantineIncidentId>> HandleAsync(
        RejectQuarantineLineCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return new QuarantineLineDisposition(quarantine, ledger, permissions, currentUser, clock).ApplyAsync(
            incidentId: command.IncidentId,
            lineNo: command.LineNo,
            quantity: command.Quantity,
            kind: QuarantineDisposition.Rejected,
            reasonCode: AdjustmentReasonCode.SupplierReturn,
            note: command.Note,
            requiredPermission: Permissions.Quarantine.Reject,
            additionalPermission: null,
            cancellationToken);
    }
}

/// <summary>
/// Handles <see cref="WriteOffQuarantineLineCommand"/>. Writes part or all of an
/// identified line off to the EXT-WRITEOFF counterparty under the caller's
/// shrinkage reason. Because the rules engine treats rejections and write-offs
/// alike, the handler separately demands <c>inventory.adjust.approve</c> at the
/// incident's location — the same second-check pattern the transfer dispatch uses.
/// </summary>
public sealed class WriteOffQuarantineLineCommandHandler(
    IQuarantineRepository quarantine,
    IInventoryLedger ledger,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<WriteOffQuarantineLineCommand, QuarantineIncidentId>
{
    /// <inheritdoc />
    public Task<Result<QuarantineIncidentId>> HandleAsync(
        WriteOffQuarantineLineCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!AllowedWriteOffReasons.Contains(command.ReasonCode))
        {
            return Task.FromResult(
                Result<QuarantineIncidentId>.Failure(QuarantineErrors.WriteOffReasonNotAllowed(command.ReasonCode)));
        }

        return new QuarantineLineDisposition(quarantine, ledger, permissions, currentUser, clock).ApplyAsync(
            incidentId: command.IncidentId,
            lineNo: command.LineNo,
            quantity: command.Quantity,
            kind: QuarantineDisposition.WrittenOff,
            reasonCode: command.ReasonCode,
            note: command.Note,
            requiredPermission: Permissions.Quarantine.Reject,
            additionalPermission: Permissions.Inventory.ApproveAdjustment,
            cancellationToken);
    }
}

/// <summary>
/// The shared identification and entry pipeline behind the link and register
/// commands: the incident is loaded, the lot is validated against the product,
/// the domain transition runs, and the QuarantineEntry ledger group posts for
/// the line.
/// </summary>
internal static class QuarantineIdentityPoster
{
    public static async Task<Result<QuarantineIncident>> LoadAsync(
        IQuarantineRepository quarantine,
        QuarantineIncidentId incidentId,
        CancellationToken cancellationToken)
    {
        QuarantineIncident? incident = await quarantine
            .GetByIdAsync(incidentId, cancellationToken)
            .ConfigureAwait(false);

        return incident is null
            ? Result<QuarantineIncident>.Failure(QuarantineErrors.IncidentUnknown(incidentId))
            : Result<QuarantineIncident>.Success(incident);
    }

    public static async Task<Result<QuarantineIncidentId>> IdentifyAndEnterAsync(
        IQuarantineRepository quarantine,
        IInventoryLedger ledger,
        QuarantineIncident incident,
        int lineNo,
        ProductId productId,
        bool registered,
        BatchId? batchId,
        string? note,
        UserId actor,
        ICurrentUser currentUser,
        ISystemClock clock,
        bool requireBarcodeOwnership,
        CancellationToken cancellationToken)
    {
        QuarantineIncidentLine? line = incident.Lines.FirstOrDefault(l => l.LineNo == lineNo);

        if (line is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.LineUnknown(lineNo));
        }

        Product? product = (await quarantine
            .GetProductsAsync([productId], cancellationToken)
            .ConfigureAwait(false)).FirstOrDefault(p => p.Id == productId);

        if (product is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.ProductUnknown(productId));
        }

        // Linking never re-points a barcode: the scanned code must already
        // belong to the named product, or the identification is refused.
        if (requireBarcodeOwnership && !product.Barcodes.Any(b => b.Value == line.Barcode))
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.BarcodeNotOwned(line.LineNo));
        }

        Result batchConsistent = product.TracksBatches
            ? batchId is null
                ? Result.Failure(QuarantineErrors.BatchRequired(line.LineNo))
                : Result.Success()
            : batchId is not null
                ? Result.Failure(QuarantineErrors.BatchMismatch(line.LineNo))
                : Result.Success();

        if (batchConsistent.IsFailure)
        {
            return Result<QuarantineIncidentId>.Failure(batchConsistent.Errors);
        }

        DateTimeOffset now = clock.UtcNow;

        Result identified = incident.Identify(line.LineNo, product.Id, batchId, registered, actor, now, note);

        if (identified.IsFailure)
        {
            return Result<QuarantineIncidentId>.Failure(identified.Errors);
        }

        QuarantineLocationInfo? locationInfo = await quarantine
            .GetLocationInfoAsync(incident.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (locationInfo is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.LocationUnknown(incident.LocationId));
        }

        if (locationInfo.TimeZoneId is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.TimeZoneMissing(incident.LocationId));
        }

        return await PostEntryAsync(
            quarantine,
            ledger,
            incident,
            [(line, product)],
            actor,
            currentUser.DeviceId,
            currentUser.CorrelationId,
            locationInfo.Kind,
            now,
            clock.BusinessDateFor(locationInfo.TimeZoneId),
            note,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Result<QuarantineIncidentId>> PostEntryAsync(
        IQuarantineRepository quarantine,
        IInventoryLedger ledger,
        QuarantineIncident incident,
        IReadOnlyList<(QuarantineIncidentLine Line, Product Product)> entries,
        UserId actor,
        DeviceId? deviceId,
        CorrelationId correlationId,
        LocationKind incidentLocationKind,
        DateTimeOffset occurredAtUtc,
        DateOnly businessDate,
        string? note,
        CancellationToken cancellationToken)
    {
        LocationId? supplierLocationId = await quarantine
            .GetExternalLocationIdAsync(SystemLocationCodes.ExternalSupplier, cancellationToken)
            .ConfigureAwait(false);

        if (supplierLocationId is null)
        {
            return Result<QuarantineIncidentId>.Failure(
                QuarantineErrors.ExternalLocationMissing(SystemLocationCodes.ExternalSupplier));
        }

        MovementGroupSpec group = QuarantineLedgerGroups.Entry(
            incident,
            entries,
            supplierLocationId.Value,
            incidentLocationKind,
            new LedgerActor(actor, ApprovedBy: null, deviceId, correlationId),
            occurredAtUtc,
            businessDate,
            note);

        Result<PostedMovementGroup> posted = await ledger.PostAsync(group, cancellationToken).ConfigureAwait(false);

        return posted.IsSuccess
            ? Result<QuarantineIncidentId>.Success(incident.Id)
            : Result<QuarantineIncidentId>.Failure(posted.Errors);
    }
}

/// <summary>
/// Shared disposition pipeline for release, reject and write-off: the incident
/// is loaded, the caller's permissions are re-checked against the incident's
/// location, the domain transition runs, and the balanced ledger group posts
/// atomically in the same unit of work.
/// </summary>
internal sealed class QuarantineLineDisposition(
    IQuarantineRepository quarantine,
    IInventoryLedger ledger,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock)
{
    private static readonly Error PermissionDenied = Error.Forbidden(
        "auth.permission_denied",
        "You do not have permission to perform this action.");

    public async Task<Result<QuarantineIncidentId>> ApplyAsync(
        QuarantineIncidentId incidentId,
        int lineNo,
        decimal quantity,
        QuarantineDisposition kind,
        AdjustmentReasonCode? reasonCode,
        string? note,
        string requiredPermission,
        string? additionalPermission,
        CancellationToken cancellationToken)
    {
        UserId actor = currentUser.UserId ?? UserId.Empty;
        DateTimeOffset now = clock.UtcNow;

        QuarantineIncident? incident = await quarantine
            .GetByIdAsync(incidentId, cancellationToken)
            .ConfigureAwait(false);

        if (incident is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentUnknown(incidentId));
        }

        if (!await permissions
                .HasPermissionAsync(actor, requiredPermission, incident.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.IncidentOutsideScope(incident.Id));
        }

        if (additionalPermission is not null
            && !await permissions
                .HasPermissionAsync(actor, additionalPermission, incident.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<QuarantineIncidentId>.Failure(PermissionDenied);
        }

        QuarantineIncidentLine? line = incident.Lines.FirstOrDefault(l => l.LineNo == lineNo);

        if (line is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.LineUnknown(lineNo));
        }

        Result transitioned = kind switch
        {
            QuarantineDisposition.Released => incident.Release(lineNo, quantity, actor, now, note),
            QuarantineDisposition.Rejected => incident.Reject(lineNo, quantity, actor, now, note),
            _ => incident.WriteOff(lineNo, quantity, actor, now, note),
        };

        if (transitioned.IsFailure)
        {
            return Result<QuarantineIncidentId>.Failure(transitioned.Errors);
        }

        if (line.ProductId is not { } productId)
        {
            // Unreachable: the domain transition already refuses unidentified
            // lines. Guarded anyway so the ledger group cannot be built with an
            // empty product.
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.LineNotIdentified(lineNo));
        }

        Product? product = (await quarantine
            .GetProductsAsync([productId], cancellationToken)
            .ConfigureAwait(false)).FirstOrDefault(p => p.Id == productId);

        if (product is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.ProductUnknown(productId));
        }

        QuarantineLocationInfo? locationInfo = await quarantine
            .GetLocationInfoAsync(incident.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (locationInfo is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.LocationUnknown(incident.LocationId));
        }

        if (locationInfo.TimeZoneId is null)
        {
            return Result<QuarantineIncidentId>.Failure(QuarantineErrors.TimeZoneMissing(incident.LocationId));
        }

        string? destinationSystemCode = kind switch
        {
            QuarantineDisposition.Rejected => SystemLocationCodes.ExternalSupplier,
            QuarantineDisposition.WrittenOff => SystemLocationCodes.ExternalWriteOff,
            _ => null,
        };

        LocationId? destinationLocationId = destinationSystemCode is null
            ? incident.LocationId
            : await quarantine
                .GetExternalLocationIdAsync(destinationSystemCode, cancellationToken)
                .ConfigureAwait(false);

        if (destinationLocationId is null)
        {
            return Result<QuarantineIncidentId>.Failure(
                QuarantineErrors.ExternalLocationMissing(destinationSystemCode!));
        }

        MovementGroupSpec group = QuarantineLedgerGroups.Disposition(
            incident,
            line,
            product,
            locationInfo.Kind,
            destinationLocationId.Value,
            kind,
            reasonCode,
            quantity,
            new LedgerActor(actor, ApprovedBy: actor, currentUser.DeviceId, currentUser.CorrelationId),
            now,
            clock.BusinessDateFor(locationInfo.TimeZoneId),
            note);

        Result<PostedMovementGroup> posted = await ledger.PostAsync(group, cancellationToken).ConfigureAwait(false);

        return posted.IsSuccess
            ? Result<QuarantineIncidentId>.Success(incident.Id)
            : Result<QuarantineIncidentId>.Failure(posted.Errors);
    }
}

/// <summary>
/// Builds the immutable ledger groups a quarantine incident posts at entry and
/// at disposition. Pure construction; the ledger service validates and stages.
/// </summary>
internal static class QuarantineLedgerGroups
{
    /// <summary>What a line costs on its legs, preferring the declared cost and
    /// falling back to the product master cost.</summary>
    public static decimal UnitCost(QuarantineIncidentLine line, Product product)
        => line.UnitCost > 0m ? line.UnitCost : product.DefaultPurchaseCost;

    /// <summary>Builds the balanced EXT-SUPPLIER → Quarantine entry group.</summary>
    public static MovementGroupSpec Entry(
        QuarantineIncident incident,
        IReadOnlyList<(QuarantineIncidentLine Line, Product Product)> entries,
        LocationId supplierLocationId,
        LocationKind incidentLocationKind,
        LedgerActor actor,
        DateTimeOffset occurredAtUtc,
        DateOnly businessDate,
        string? note)
    {
        List<MovementLegSpec> legs = new(entries.Count * 2);

        foreach ((QuarantineIncidentLine line, Product product) in entries)
        {
            decimal unitCost = UnitCost(line, product);

            legs.Add(new MovementLegSpec(
                product.Id,
                line.BatchId,
                supplierLocationId,
                LocationKind.External,
                InventoryState.External,
                -line.Quantity,
                unitCost,
                product.TracksBatches));

            legs.Add(new MovementLegSpec(
                product.Id,
                line.BatchId,
                incident.LocationId,
                incidentLocationKind,
                InventoryState.Quarantine,
                +line.Quantity,
                unitCost,
                product.TracksBatches));
        }

        return new MovementGroupSpec(
            EventId.New(),
            InventoryMovementType.QuarantineEntry,
            ReferenceDocumentType.QuarantineIncident,
            incident.Id.Value,
            incident.Number,
            legs,
            actor,
            occurredAtUtc,
            businessDate,
            ReasonCode: null,
            Notes: note);
    }

    /// <summary>Builds the balanced disposition group out of quarantine.</summary>
    public static MovementGroupSpec Disposition(
        QuarantineIncident incident,
        QuarantineIncidentLine line,
        Product product,
        LocationKind incidentLocationKind,
        LocationId destinationLocationId,
        QuarantineDisposition kind,
        AdjustmentReasonCode? reasonCode,
        decimal quantity,
        LedgerActor actor,
        DateTimeOffset occurredAtUtc,
        DateOnly businessDate,
        string? note)
    {
        bool leavingTheBusiness = kind != QuarantineDisposition.Released;

        InventoryMovementType movementType = kind == QuarantineDisposition.Released
            ? InventoryMovementType.QuarantineRelease
            : InventoryMovementType.QuarantineReject;

        InventoryState destinationState = kind == QuarantineDisposition.Released
            ? InventoryState.Available
            : InventoryState.External;

        decimal unitCost = UnitCost(line, product);

        return new MovementGroupSpec(
            EventId.New(),
            movementType,
            ReferenceDocumentType.QuarantineIncident,
            incident.Id.Value,
            incident.Number,
            [
                new MovementLegSpec(
                    product.Id,
                    line.BatchId,
                    incident.LocationId,
                    incidentLocationKind,
                    InventoryState.Quarantine,
                    -quantity,
                    unitCost,
                    product.TracksBatches),
                new MovementLegSpec(
                    product.Id,
                    line.BatchId,
                    destinationLocationId,
                    leavingTheBusiness ? LocationKind.External : incidentLocationKind,
                    destinationState,
                    +quantity,
                    unitCost,
                    product.TracksBatches),
            ],
            actor,
            occurredAtUtc,
            businessDate,
            ReasonCode: reasonCode,
            Notes: note);
    }
}

/// <summary>The shrinkage reasons a write-off may carry.</summary>
internal static class AllowedWriteOffReasons
{
    public static IReadOnlySet<AdjustmentReasonCode> Values { get; } = new HashSet<AdjustmentReasonCode>
    {
        AdjustmentReasonCode.Damaged,
        AdjustmentReasonCode.Expired,
        AdjustmentReasonCode.Spoilage,
        AdjustmentReasonCode.Loss,
        AdjustmentReasonCode.Theft,
        AdjustmentReasonCode.Broken,
        AdjustmentReasonCode.Contaminated,
    };

    public static bool Contains(AdjustmentReasonCode reason) => Values.Contains(reason);
}