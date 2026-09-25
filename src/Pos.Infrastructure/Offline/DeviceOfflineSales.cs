using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Offline;

/// <summary>A cash sale to complete on the register while head office is unreachable.</summary>
/// <param name="ShiftId">The shift it is rung up in; it must be the cashier's own open shift here.</param>
/// <param name="BusinessDate">The shift's business date.</param>
/// <param name="CustomerId">The customer, when one was attached.</param>
/// <param name="CustomerName">The customer's display name, for the receipt.</param>
/// <param name="Lines">What was sold, at the cached price.</param>
/// <param name="Payments">How it was paid. Offline, only cash is accepted.</param>
/// <param name="Number">
/// The SAL number, when one was already allocated for this sale — it was first
/// sent to head office and the answer never came back. Null allocates a new one.
/// </param>
/// <param name="EventId">The event identity it was first sent under, with <paramref name="Number"/>.</param>
public sealed record OfflineSaleRequest(
    CashierShiftId ShiftId,
    DateOnly BusinessDate,
    Guid? CustomerId,
    string? CustomerName,
    IReadOnlyList<OfflineSaleLine> Lines,
    IReadOnlyList<OfflineSalePayment> Payments,
    string? Number = null,
    Guid? EventId = null);

/// <summary>One line of an offline sale.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="UnitOfMeasureId">The unit it is sold in.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="Name">Its name.</param>
/// <param name="Barcode">The scanned barcode, if any.</param>
/// <param name="Quantity">The quantity.</param>
/// <param name="UnitPrice">The cached price in force.</param>
public sealed record OfflineSaleLine(
    Guid ProductId,
    Guid UnitOfMeasureId,
    string Sku,
    string Name,
    string? Barcode,
    decimal Quantity,
    decimal UnitPrice);

/// <summary>One payment on an offline sale.</summary>
/// <param name="Method">The rail.</param>
/// <param name="Amount">The amount applied.</param>
/// <param name="Tendered">The cash handed over.</param>
public sealed record OfflineSalePayment(PaymentMethod Method, decimal Amount, decimal? Tendered);

/// <summary>Why the register refused to complete a sale offline.</summary>
public static class OfflineSaleErrors
{
    /// <summary>Nobody is signed in at the register.</summary>
    public static readonly Error NotSignedIn = Error.Forbidden(
        "offline_sale.not_signed_in", "Sign in before using the till.");

    /// <summary>The cashier's cached authority does not include selling here.</summary>
    public static readonly Error NotAuthorized = Error.Forbidden(
        "offline_sale.not_authorized",
        "Your offline access on this register doesn’t allow sales, or it has expired. Connect to head office and sign in to renew it.");

    /// <summary>The sale has nothing on it.</summary>
    public static readonly Error Empty = Error.Validation(
        "offline_sale.empty", "Add an item before taking payment.");

    /// <summary>A line is malformed.</summary>
    public static readonly Error LineInvalid = Error.Validation(
        "offline_sale.line_invalid", "Every line needs a product, a unit, a quantity above zero and a price.");

    /// <summary>A non-cash tender while offline.</summary>
    public static readonly Error CashOnly = Error.Validation(
        "offline_sale.cash_only",
        "Only cash can be taken while head office can’t be reached. Card and e-wallet payments need a connection.");

    /// <summary>The payments do not settle the sale exactly.</summary>
    public static readonly Error PaymentMismatch = Error.Validation(
        "offline_sale.payment_mismatch", "The payment must equal the amount due.");

    /// <summary>Less cash was handed over than was applied.</summary>
    public static readonly Error TenderShort = Error.Validation(
        "offline_sale.tender_short", "The cash handed over is less than the amount due.");

    /// <summary>The shift is not this cashier's open shift on this register.</summary>
    public static readonly Error ShiftNotOpenHere = Error.Conflict(
        "offline_sale.shift_not_open",
        "This register has no open shift of yours to record the sale in. Open a shift first.");

    /// <summary>A supplied number was not allocated by this register.</summary>
    public static readonly Error NumberInvalid = Error.Validation(
        "offline_sale.number_invalid", "The sale number was not issued by this register.");
}

/// <summary>
/// Completes cash sales on the register while head office cannot be reached
/// (OFFLINE_SYNC.md §1). The register records what it rang up and queues the
/// sale for head office, in one local transaction.
/// </summary>
/// <remarks>
/// <para>
/// The device does not run the server's sale use case: it has no ledger, no
/// batches and no live price history. What it does is what the online path
/// relies on the register for anyway — number the sale under its own code,
/// check the cashier may sell here and owns the open shift — and then queue the
/// same command input the online path sends, as a <see cref="SyncEventType.SaleCompleted"/>
/// event. Head office replays that through the shared sale command, so price,
/// VAT and stock are settled exactly as they would have been online, and
/// anything the replay refuses lands on its sync-failure queue rather than
/// being lost (OFFLINE_SYNC.md §7: physical reality is recorded; authority is
/// re-verified).
/// </para>
/// <para>
/// Authority comes from the cached snapshot and is re-checked on every sale, so
/// a register whose snapshot has expired stops selling even while someone is
/// still signed in. Only cash is taken: a card or wallet payment needs the
/// provider, which needs the connection.
/// </para>
/// </remarks>
/// <param name="database">The encrypted device store.</param>
/// <param name="session">Who is signed in at the register.</param>
/// <param name="permissions">The cached-snapshot permission evaluator.</param>
/// <param name="profile">This device's enrolled identity.</param>
/// <param name="clock">The device clock.</param>
public sealed class DeviceOfflineSales(
    DeviceDatabaseInitializer database,
    DeviceSession session,
    IPermissionEvaluator permissions,
    IDeviceProfileAccessor profile,
    ISystemClock clock)
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    /// <summary>Completes a cash sale on the register and queues it for head office.</summary>
    /// <param name="request">The sale.</param>
    /// <param name="cashierName">The cashier's display name, for the receipt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recorded sale, or why it was refused. A refused sale leaves nothing behind.</returns>
    public async Task<Result<DeviceLocalSale>> RecordAsync(
        OfflineSaleRequest request,
        string cashierName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (session.UserId is not { } cashier
            || session.DeviceId is not { } device
            || session.LocationId is not { } location)
        {
            return Result<DeviceLocalSale>.Failure(OfflineSaleErrors.NotSignedIn);
        }

        Result<decimal> total = Validate(request);
        if (total.IsFailure)
        {
            return Result<DeviceLocalSale>.Failure(total.Error);
        }

        if (!await permissions
                .HasPermissionAsync(cashier, Permissions.Sales.Create, location, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<DeviceLocalSale>.Failure(OfflineSaleErrors.NotAuthorized);
        }

        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (request.EventId is { } retried)
        {
            // The same sale queued twice — a retry after the first attempt was
            // recorded — is the first sale, not a second one.
            DeviceLocalSale? existing = await context.LocalSales
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.EventId == new EventId(retried), cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return Result<DeviceLocalSale>.Success(existing);
            }
        }

        await using IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        CashierShift? shift = await context.LocalShifts
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.ShiftId, cancellationToken)
            .ConfigureAwait(false);

        // The same three checks head office makes: the shift is open, it is on
        // this register, and the person selling is the person who opened it.
        if (shift is null
            || shift.Status != ShiftStatus.Open
            || shift.DeviceId != device
            || shift.CashierUserId != cashier)
        {
            return Result<DeviceLocalSale>.Failure(OfflineSaleErrors.ShiftNotOpenHere);
        }

        DeviceStoreProfile enrolled = await profile.GetAsync(cancellationToken).ConfigureAwait(false);

        DocumentNumber number;
        if (request.Number is { } allocated)
        {
            Result<DocumentNumber> parsed = DocumentNumber.Parse(allocated);
            if (parsed.IsFailure
                || !string.Equals(parsed.Value.DeviceShortCode, enrolled.ShortCode, StringComparison.OrdinalIgnoreCase))
            {
                return Result<DeviceLocalSale>.Failure(OfflineSaleErrors.NumberInvalid);
            }

            number = parsed.Value;
        }
        else
        {
            // Joins this transaction, so a sale that does not commit gives its
            // number back instead of leaving a gap on the receipt roll.
            number = await new DeviceDocumentNumberGenerator(context, profile, clock)
                .NextAsync(DocumentType.Sale, cancellationToken)
                .ConfigureAwait(false);
        }

        EventId eventId = request.EventId is { } supplied ? new EventId(supplied) : EventId.New();
        DateTimeOffset completedAt = clock.UtcNow;

        string currency = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == location)
            .Select(l => l.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? "PHP";

        DeviceLocalSale sale = new(
            eventId,
            number.Value,
            location,
            request.ShiftId,
            device,
            cashier,
            cashierName,
            request.CustomerId,
            request.CustomerName,
            request.BusinessDate,
            completedAt,
            total.Value,
            currency,
            [.. request.Lines.Select(line => new DeviceLocalSaleLine(
                line.ProductId,
                line.UnitOfMeasureId,
                line.Sku,
                line.Name,
                line.Barcode,
                line.Quantity,
                line.UnitPrice,
                LineTotal(line)))],
            [.. request.Payments.Select(payment => new DeviceLocalSalePayment(
                payment.Method,
                payment.Amount,
                payment.Tendered,
                payment.Tendered is { } tendered ? Math.Max(0m, tendered - payment.Amount) : 0m))]);

        context.LocalSales.Add(sale);

        // Exactly what the online path sends to the sale command, so head office
        // replays the sale it would have been given at the counter.
        SaleSyncPayload payload = new(
            number.Value,
            location.Value,
            request.ShiftId.Value,
            device.Value,
            cashier.Value,
            request.CustomerId,
            request.BusinessDate,
            completedAt,
            [.. request.Lines.Select(line => new SaleSyncLine(
                line.ProductId,
                line.Quantity,
                line.UnitOfMeasureId,
                line.Barcode,
                UnitPriceOverride: null,
                PriceOverrideAuthorizedByUserId: null,
                Discount: 0m,
                DiscountAuthorizedByUserId: null,
                AllowExpiredOverride: false,
                ExpiredOverrideReason: null))],
            [.. request.Payments.Select(payment => new SaleSyncPayment(
                payment.Method, payment.Amount, payment.Tendered, ProviderReference: null))]);

        DeviceCurrentUser currentUser = new(session);
        await new DeviceOutbox(context, currentUser, profile, clock)
            .EnqueueAsync(SyncEventType.SaleCompleted, payload, location, cancellationToken, eventId)
            .ConfigureAwait(false);

        await new DeviceAuditWriter(context, currentUser, clock)
            .WriteAsync(
                new AuditEntry(
                    AuditActions.Sales.SaleCompleted,
                    "sale",
                    eventId.Value,
                    NewValueJson: JsonSerializer.Serialize(
                        new
                        {
                            Number = number.Value,
                            ShiftId = request.ShiftId.Value,
                            NetTotal = total.Value,
                            LineCount = request.Lines.Count,
                            Offline = true,
                        },
                        AuditJson),
                    LocationId: location),
                cancellationToken)
            .ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result<DeviceLocalSale>.Success(sale);
    }

    /// <summary>Reads a sale this register completed offline.</summary>
    /// <param name="number">Its SAL number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sale, or null when this register did not record one under that number.</returns>
    public async Task<DeviceLocalSale?> FindAsync(string number, CancellationToken cancellationToken = default)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.LocalSales
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Number == number, cancellationToken)
            .ConfigureAwait(false);
    }

    private static decimal LineTotal(OfflineSaleLine line)
        => decimal.Round(line.UnitPrice * line.Quantity, Money.StorageScale, Money.IntermediateRounding);

    private static Result<decimal> Validate(OfflineSaleRequest request)
    {
        if (request.Lines is not { Count: > 0 })
        {
            return Result<decimal>.Failure(OfflineSaleErrors.Empty);
        }

        if (request.Lines.Any(line => line.ProductId == Guid.Empty
                                      || line.UnitOfMeasureId == Guid.Empty
                                      || line.Quantity <= 0m
                                      || line.UnitPrice < 0m))
        {
            return Result<decimal>.Failure(OfflineSaleErrors.LineInvalid);
        }

        if (request.Payments is not { Count: > 0 } || request.Payments.Any(p => p.Method != PaymentMethod.Cash))
        {
            return Result<decimal>.Failure(OfflineSaleErrors.CashOnly);
        }

        decimal total = decimal.Round(
            request.Lines.Sum(LineTotal), Money.StorageScale, Money.IntermediateRounding);
        decimal paid = decimal.Round(
            request.Payments.Sum(p => p.Amount), Money.StorageScale, Money.IntermediateRounding);

        if (paid != total)
        {
            return Result<decimal>.Failure(OfflineSaleErrors.PaymentMismatch);
        }

        if (request.Payments.Any(p => p.Tendered is not { } tendered || tendered < p.Amount))
        {
            return Result<decimal>.Failure(OfflineSaleErrors.TenderShort);
        }

        return Result<decimal>.Success(total);
    }
}
