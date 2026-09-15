using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Handles <see cref="ReprintSaleReceiptCommand"/>. Reprinting does not change
/// the sale, the shift or the ledger: the device re-emits the receipt from its
/// local copy. The handler verifies the sale exists, is still completed, and
/// belongs to the request's location, then appends a <c>sale.receipt.reprinted</c>
/// audit record together with a print-log entry carrying the reason — all inside
/// the unit-of-work transaction.
/// </summary>
/// <remarks>
/// A reprint requires no open shift (POS.md §4): it is a read-side emission, not
/// a cash event. The reason is mandatory so every re-emission is accountable.
/// </remarks>
public sealed class ReprintSaleReceiptCommandHandler(
    ISalesRepository repository,
    IAuditWriter audit) : ICommandHandler<ReprintSaleReceiptCommand, SaleId>
{
    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public async Task<Result<SaleId>> HandleAsync(
        ReprintSaleReceiptCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ------------------------------------------------------------------
        // 1. Load the sale and verify the request matches its facts.
        // ------------------------------------------------------------------
        Sale? sale = await repository
            .GetByIdAsync(command.SaleId, cancellationToken)
            .ConfigureAwait(false);

        if (sale is null)
        {
            return Result<SaleId>.Failure(ReprintCommandErrors.SaleNotFound(command.SaleId));
        }

        if (command.LocationId != sale.LocationId)
        {
            return Result<SaleId>.Failure(ReprintCommandErrors.LocationMismatch(sale.Id, command.LocationId));
        }

        // A voided sale has no valid receipt to re-emit: its tender has been
        // reversed and the document is inert.
        if (sale.Status != SaleStatus.Completed)
        {
            return Result<SaleId>.Failure(ReprintCommandErrors.UnreprintableState(sale.Id, sale.Status));
        }

        // ------------------------------------------------------------------
        // 2. Record the reprint: log entry + audit record.
        // ------------------------------------------------------------------
        Result<SaleReceiptPrint> print = SaleReceiptPrint.Create(
            sale.Id,
            command.ReprintedByUserId,
            command.ReprintedAtUtc,
            isReprint: true,
            command.Reason);

        if (print.IsFailure)
        {
            return Result<SaleId>.Failure(print.Errors);
        }

        string auditJson = JsonSerializer.Serialize(new
        {
            sale.Id,
            sale.Number,
            LocationId = command.LocationId.Value,
            DeviceId = command.DeviceId.Value,
            PrintedAtUtc = command.ReprintedAtUtc,
            PrintedByUserId = command.ReprintedByUserId.Value,
            Reason = command.Reason,
        }, JsonDefaults);

        await audit.WriteAsync(new AuditEntry(
            AuditActions.Sales.ReceiptReprinted,
            "sale",
            sale.Id.Value,
            NewValueJson: auditJson,
            Reason: command.Reason,
            ReferenceDocumentType: ReferenceDocumentType.Sale,
            ReferenceDocumentId: sale.Id.Value,
            LocationId: command.LocationId),
            cancellationToken).ConfigureAwait(false);

        Result<ReceiptPrintId> saved = await repository
            .AddReceiptPrintAsync(print.Value, cancellationToken)
            .ConfigureAwait(false);

        return saved.IsSuccess
            ? Result<SaleId>.Success(sale.Id)
            : Result<SaleId>.Failure(saved.Errors);
    }
}