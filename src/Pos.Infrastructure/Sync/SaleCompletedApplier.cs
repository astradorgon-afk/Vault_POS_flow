using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Applies a sale a device rang up offline, by replaying it through the same
/// handler the server runs online (ADR-0008).
/// </summary>
/// <remarks>
/// <para>
/// The event carries what the cashier asked for — the lines and the payments —
/// and not what the device worked out from them. The server re-resolves the
/// effective price, the VAT classification, the FEFO allocation and the cash
/// rounding from its own data, because a sale recorded centrally must never
/// trust a register for its numbers. Replaying the command is what makes that
/// automatic rather than a second implementation that would drift.
/// </para>
/// <para>
/// The handler is called directly rather than dispatched, because the pipeline's
/// authorization behaviour would evaluate the *device* — the principal that
/// uploaded the batch — where what matters is the cashier who rang the sale up.
/// That check is made here instead, and its failure does not refuse the sale:
/// the money has already changed hands, so a sale by a cashier whose authority
/// was withdrawn while the device was offline lands and is flagged for review
/// (OFFLINE_SYNC.md §7). Refusing it would destroy the only central record of
/// goods that already left the shelf.
/// </para>
/// <para>
/// A sale the server prices differently is refused today rather than recorded
/// at the price charged, because the payments no longer settle the re-priced
/// total. OFFLINE_SYNC.md §7 wants it accepted with the difference noted, and
/// closing that gap means carrying the price row the device quoted, so the line
/// is recorded against that version rather than re-priced or dressed up as a
/// manual override. Until then the device escalates the refusal as a
/// SyncFailure and keeps the event, so the record is parked rather than lost.
/// </para>
/// <para>
/// The server mints its own <c>SaleId</c>, and the SAL number is what ties the
/// two sides together: it is unique, device-scoped so it cannot collide, and
/// already printed on the customer's receipt. Later events about the same sale —
/// a void, a reprint, a return — name it by that number.
/// </para>
/// </remarks>
/// <param name="context">The server database.</param>
/// <param name="handler">The one sale handler, shared with the online path.</param>
/// <param name="permissions">Evaluated now, not as of the sale.</param>
/// <param name="audit">The audit trail.</param>
public sealed class SaleCompletedApplier(
    PosDbContext context,
    ICommandHandler<CompleteSaleCommand, SaleId> handler,
    IPermissionEvaluator permissions,
    IAuditWriter audit) : ISyncEventApplier
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions AuditOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <inheritdoc />
    public string EventType => "SaleCompleted";

    /// <inheritdoc />
    public async Task<SyncApplyResult> ApplyAsync(
        DeviceId deviceId,
        EventId eventId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        SaleSyncPayload? upload;

        try
        {
            upload = JsonSerializer.Deserialize<SaleSyncPayload>(payloadJson, ReadOptions);
        }
        catch (JsonException)
        {
            upload = null;
        }

        if (upload is null || upload.Lines is null || upload.Payments is null)
        {
            return SyncApplyResult.Rejected("sync.payload_invalid", "The sale payload could not be read.");
        }

        if (upload.DeviceId != deviceId.Value)
        {
            return SyncApplyResult.Rejected(
                "sync.device_mismatch",
                "The event names a different device than the one that uploaded it.");
        }

        if (upload.Lines.Count == 0)
        {
            return SyncApplyResult.Rejected("sync.payload_invalid", "A sale must carry at least one line.");
        }

        bool alreadyHeld = await context.Sales
            .AsNoTracking()
            .AnyAsync(s => s.Number == upload.Number, cancellationToken)
            .ConfigureAwait(false);

        if (alreadyHeld)
        {
            // The number is the device's and already on the customer's receipt,
            // so the same receipt arriving under a second event identifier is a
            // device fault rather than a second sale.
            return SyncApplyResult.Rejected(
                "sync.sale_already_held",
                "This sale is already recorded centrally.");
        }

        Result<DocumentNumber> number = DocumentNumber.Parse(upload.Number);

        if (number.IsFailure)
        {
            return SyncApplyResult.Rejected("sync.payload_invalid", "The sale number is not a valid document number.");
        }

        Result<CompleteSaleCommand> rebuilt = Rebuild(upload, deviceId, number.Value);

        if (rebuilt.IsFailure)
        {
            return SyncApplyResult.Rejected(rebuilt.Error.Code, rebuilt.Error.Message);
        }

        LocationId locationId = new(upload.LocationId);
        UserId cashierId = new(upload.CompletedByUserId);

        // Evaluated now, not as of the sale: a cashier who lost the authority
        // while the device was offline is the case this exists for.
        bool mayStillSell = await permissions
            .HasPermissionAsync(cashierId, Permissions.Sales.Create, locationId, cancellationToken)
            .ConfigureAwait(false);

        Result<SaleId> applied = await handler
            .HandleAsync(rebuilt.Value, cancellationToken)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {
            return SyncApplyResult.Rejected(applied.Error.Code, applied.Error.Message);
        }

        // Recorded whether the ledger refused it or let it through, so this
        // finds the oversells that actually happened, not the ones that did not.
        bool soldIntoNegativeStock = await context.NegativeStockAttempts
            .AsNoTracking()
            .AnyAsync(a => a.EventId == new EventId(upload.EventId), cancellationToken)
            .ConfigureAwait(false);

        if (soldIntoNegativeStock)
        {
            return SyncApplyResult.RequiresReview(
                upload.Number,
                "sync.sold_into_negative_stock",
                "The sale was recorded, and it took the shelf below zero. Somebody has to count it.");
        }

        // A product withdrawn while the register was dark. The sale stands — the
        // goods left the shelf — and the product stays withdrawn, so the till
        // stops offering it as soon as the feed reaches it. What is left is to
        // tell somebody it went out after the decision to stop selling it
        // (OFFLINE_SYNC.md §7).
        List<ProductId> soldProducts = [.. upload.Lines.Select(l => new ProductId(l.ProductId)).Distinct()];

        bool soldSomethingWithdrawn = await context.Products
            .AsNoTracking()
            .AnyAsync(p => soldProducts.Contains(p.Id) && !p.IsActive, cancellationToken)
            .ConfigureAwait(false);

        if (soldSomethingWithdrawn)
        {
            return SyncApplyResult.RequiresReview(
                upload.Number,
                "sync.sold_a_withdrawn_product",
                "The sale was recorded, and it includes a product that has since been withdrawn from sale.");
        }

        if (!mayStillSell)
        {
            // The audit writer stamps the actor from the request context, which
            // here is whoever was signed in when the register uploaded — not the
            // cashier the entry is about. Naming them explicitly is the whole
            // point of the record.
            await audit.WriteAsync(new AuditEntry(
                AuditActions.Sync.RequiresReview,
                "sale",
                applied.Value.Value,
                NewValueJson: JsonSerializer.Serialize(
                    new
                    {
                        Number = upload.Number,
                        CashierUserId = cashierId.Value,
                        LocationId = locationId.Value,
                    },
                    AuditOptions),
                Reason: "Rung up offline by a cashier who no longer holds sale.create at this location.",
                LocationId: locationId),
                cancellationToken).ConfigureAwait(false);

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return SyncApplyResult.RequiresReview(
                upload.Number,
                "sync.cashier_permission_withdrawn",
                "The sale was recorded, but the cashier no longer holds the authority to sell here.");
        }

        return SyncApplyResult.Accepted(upload.Number);
    }

    /// <summary>Rebuilds the command the device executed.</summary>
    private static Result<CompleteSaleCommand> Rebuild(
        SaleSyncPayload upload,
        DeviceId deviceId,
        DocumentNumber number)
    {
        List<CompleteSalePayment> payments = new(upload.Payments.Count);

        foreach (SalePaymentSyncPayload payment in upload.Payments)
        {
            if (!Enum.TryParse(payment.Method, ignoreCase: false, out PaymentMethod method))
            {
                return Result<CompleteSaleCommand>.Failure(Error.Validation(
                    "sync.payload_invalid",
                    FormattableString.Invariant($"{payment.Method} is not a payment method this server knows.")));
            }

            payments.Add(new CompleteSalePayment(method, payment.Amount, payment.Tendered, payment.ProviderReference));
        }

        List<CompleteSaleLine> lines = new(upload.Lines.Count);

        foreach (SaleLineSyncPayload line in upload.Lines)
        {
            lines.Add(new CompleteSaleLine(
                new ProductId(line.ProductId),
                line.Quantity,
                new UnitOfMeasureId(line.UnitOfMeasureId),
                line.Barcode,
                line.UnitPriceOverride,
                line.PriceOverrideAuthorizedByUserId is { } priceAuthorizer ? new UserId(priceAuthorizer) : null,
                line.Discount,
                line.DiscountAuthorizedByUserId is { } discountAuthorizer ? new UserId(discountAuthorizer) : null,

                // Selling from an expired batch needs sale.expired_override,
                // which is not offline-capable, so it can never have happened on
                // a device (OFFLINE_SYNC.md §4). Passing it through would let a
                // crafted payload reach a path the till itself cannot.
                AllowExpiredOverride: false,
                ExpiredOverrideReason: null,

                // The row the till charged from. The server reads the amount
                // back off it, so a stale price is recorded as what it was
                // rather than re-priced or passed off as a manual override.
                line.QuotedPriceVersion is { } quotedVersion ? new ProductPriceId(quotedVersion) : null));
        }

        return Result<CompleteSaleCommand>.Success(new CompleteSaleCommand(
            number,
            new EventId(upload.EventId),
            new LocationId(upload.LocationId),
            new CashierShiftId(upload.ShiftId),
            deviceId,
            new UserId(upload.CompletedByUserId),
            upload.CustomerId is { } customerId ? new CustomerId(customerId) : null,
            upload.BusinessDate,
            upload.CompletedAtUtc,
            lines,
            payments,

            // The goods left the shelf at a till that could not ask us. The
            // ledger post is marked for review, so a location configured for it
            // accepts a draw its shelf cannot cover rather than refusing a sale
            // that is already a fact.
            ReplayedOffline: true));
    }
}
