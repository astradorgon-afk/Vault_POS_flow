using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Quarantine;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Quarantine;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Atomically records an inspection, its ledger legs, optional quarantine incident, and audit entry.</summary>
public sealed class DisposeSalesReturnCommandHandler(
    IReturnDispositionRepository repository,
    IQuarantineRepository quarantine,
    IDocumentNumberGenerator numbers,
    IInventoryLedger ledger,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<DisposeSalesReturnCommand, EventId>
{
    /// <inheritdoc />
    public async Task<Result<EventId>> HandleAsync(DisposeSalesReturnCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        SalesReturn? salesReturn = await repository.GetReturnAsync(command.SalesReturnId, cancellationToken).ConfigureAwait(false);
        if (salesReturn is null)
        {
            return Result<EventId>.Failure(ReturnDispositionErrors.ReturnUnknown);
        }
        if (salesReturn.LocationId != command.LocationId)
        {
            return Result<EventId>.Failure(ReturnDispositionErrors.LocationMismatch);
        }

        SalesReturnDisposition? existing = await repository.GetEventAsync(command.EventId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            bool matches = existing.SalesReturnId == salesReturn.Id
                && salesReturn.Items.Any(i => i.Id == existing.SalesReturnItemId && i.LineNumber == command.LineNumber)
                && existing.Quantity == command.Quantity && existing.Kind == command.Kind
                && existing.ReasonCode == command.ReasonCode && existing.Note == command.Note.Trim();
            return matches ? Result<EventId>.Success(existing.Id) : Result<EventId>.Failure(ReturnDispositionErrors.EventConflict);
        }

        QuarantineLocationInfo? location = await quarantine.GetLocationInfoAsync(command.LocationId, cancellationToken).ConfigureAwait(false);
        if (location is null || location.Kind == LocationKind.External)
        {
            return Result<EventId>.Failure(SaleCommandErrors.LocationUnknown(command.LocationId));
        }

        UserId actor = currentUser.UserId ?? UserId.Empty;
        DateTimeOffset now = clock.UtcNow;
        DateOnly businessDate = clock.BusinessDateFor(location.TimeZoneId);
        Result<SalesReturnDisposition> decision = salesReturn.DisposeLine(command.EventId, command.LineNumber,
            command.Quantity, command.Kind, command.ReasonCode, command.Note, actor, now, businessDate);
        if (decision.IsFailure)
        {
            return Result<EventId>.Failure(decision.Errors);
        }

        SalesReturnItem item = salesReturn.Items.Single(i => i.LineNumber == command.LineNumber);
        InventoryState targetState = command.Kind switch
        {
            ReturnDispositionKind.Restock => InventoryState.Available,
            ReturnDispositionKind.Quarantine => InventoryState.Quarantine,
            ReturnDispositionKind.Waste => InventoryState.External,
            _ => InventoryState.Damaged,
        };
        LocationId targetLocation = salesReturn.LocationId;
        if (command.Kind == ReturnDispositionKind.Waste)
        {
            LocationId? writeOff = await quarantine.GetExternalLocationIdAsync(SystemLocationCodes.ExternalWriteOff, cancellationToken).ConfigureAwait(false);
            if (writeOff is null)
            {
                return Result<EventId>.Failure(QuarantineErrors.ExternalLocationMissing(SystemLocationCodes.ExternalWriteOff));
            }
            targetLocation = writeOff.Value;
        }

        Result<PostedMovementGroup> posted = await ledger.PostAsync(new MovementGroupSpec(
            command.EventId, InventoryMovementType.ReturnDisposition, ReferenceDocumentType.SalesReturn,
            salesReturn.Id.Value, salesReturn.Number,
            [
                new(item.ProductId, item.BatchId, salesReturn.LocationId, location.Kind,
                    InventoryState.ReturnPending, -command.Quantity, item.UnitCost, item.BatchId is not null),
                new(item.ProductId, item.BatchId, targetLocation,
                    targetState == InventoryState.External ? LocationKind.External : location.Kind,
                    targetState, command.Quantity, item.UnitCost, item.BatchId is not null),
            ],
            new LedgerActor(actor, null, currentUser.DeviceId, currentUser.CorrelationId), now, businessDate,
            command.ReasonCode, command.Note.Trim()), cancellationToken).ConfigureAwait(false);
        if (posted.IsFailure)
        {
            return Result<EventId>.Failure(posted.Errors);
        }
        if (posted.Value.WasDuplicate)
        {
            return Result<EventId>.Failure(ReturnDispositionErrors.EventConflict);
        }

        // Flush the line's concurrency check before any other repository saves.
        try
        {
            await repository.AddAsync(decision.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            return Result<EventId>.Failure(ReturnDispositionErrors.Contention);
        }
        QuarantineIncidentId? incidentId = null;
        if (command.Kind == ReturnDispositionKind.Quarantine)
        {
            DocumentNumber number = await numbers.NextAsync(DocumentType.QuarantineIncident, cancellationToken).ConfigureAwait(false);
            Result<QuarantineIncident> created = QuarantineIncident.Create(number, salesReturn.LocationId,
                [new QuarantineLineSpec(item.Barcode ?? item.ProductId.Value.ToString("N"), command.Quantity, item.UnitCost, item.ProductName)],
                actor, now, $"Return {salesReturn.Number}, line {item.LineNumber}.");
            if (created.IsFailure)
            {
                return Result<EventId>.Failure(created.Errors);
            }
            Result identified = created.Value.Identify(1, item.ProductId, item.BatchId, false, actor, now, command.Note.Trim());
            if (identified.IsFailure)
            {
                return Result<EventId>.Failure(identified.Errors);
            }
            Result<QuarantineIncidentId> added = await quarantine.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
            if (added.IsFailure)
            {
                return Result<EventId>.Failure(added.Errors);
            }
            incidentId = added.Value;
        }

        await audit.WriteAsync(new AuditEntry(AuditActions.Sales.ReturnDispositioned, "sales_return", salesReturn.Id.Value,
            NewValueJson: JsonSerializer.Serialize(new { command.EventId, command.LineNumber, command.Quantity,
                command.Kind, command.ReasonCode, command.Note, IncidentId = incidentId, posted.Value.MovementGroupId }),
            ReferenceDocumentType: ReferenceDocumentType.SalesReturn, ReferenceDocumentId: salesReturn.Id.Value,
            LocationId: salesReturn.LocationId), cancellationToken).ConfigureAwait(false);
        return Result<EventId>.Success(command.EventId);
    }
}
