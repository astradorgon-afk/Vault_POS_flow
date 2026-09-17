using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Cashier shifts, held on the device until they sync. It backs the same
/// handlers the server runs (ADR-0008): an offline shift and an online one are
/// the same business record, written by the same aggregate.
/// </summary>
/// <remarks>
/// <para>
/// The port is server-shaped, and three of its members answer questions about
/// sales — the cash a shift took, what a sale has already been refunded, which
/// shifts a background worker should force-close. The first two read the
/// device's own sale, return and refund rows, which is what lets a drawer close
/// offline against the cash it actually took rather than against zero. The
/// third refuses: force-close is a background worker's job and a device runs no
/// workers, so reaching it is a registration mistake rather than a runtime
/// condition.
/// </para>
/// <para>
/// The device context tracks by default, unlike the server's, so reads that
/// feed a mutation need no explicit opt-in here.
/// </para>
/// </remarks>
/// <param name="context">The scoped device context.</param>
/// <param name="profile">This device's enrolled identity.</param>
/// <param name="outbox">The upload queue this repository enqueues to.</param>
public sealed class DeviceShiftRepository(
    PosDeviceDbContext context,
    IDeviceProfileAccessor profile,
    IDeviceOutbox outbox) : IShiftRepository
{
    /// <inheritdoc />
    public async Task<Result<CashierShiftId>> AddAsync(CashierShift shift, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shift);

        await context.LocalShifts.AddAsync(shift, cancellationToken).ConfigureAwait(false);
        await EnqueueAsync(SyncEventType.ShiftOpened, shift, cancellationToken).ConfigureAwait(false);

        return Result<CashierShiftId>.Success(shift.Id);
    }

    /// <inheritdoc />
    public async Task<CashierShift?> GetShiftAsync(CashierShiftId shiftId, CancellationToken cancellationToken)
        => await context.LocalShifts
            .FirstOrDefaultAsync(s => s.Id == shiftId, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<CashierShift?> GetOpenShiftForDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken)
        => await context.LocalShifts
            .Where(s => s.DeviceId == deviceId
                        && (s.Status == ShiftStatus.Open || s.Status == ShiftStatus.Suspended))
            .OrderByDescending(s => s.OpenedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Result<CashierShiftId>> UpdateAsync(CashierShift shift, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shift);

        // Tracked by the read that loaded it; the unit of work writes it. What
        // the outbox needs is which business event this update was, and the
        // shift's own status is the honest answer — the alternative is a flag
        // threaded through a port the server shares.
        SyncEventType? type = shift.Status switch
        {
            ShiftStatus.Suspended => SyncEventType.ShiftSuspended,
            ShiftStatus.Open => SyncEventType.ShiftResumed,
            ShiftStatus.Closed => SyncEventType.ShiftClosed,
            _ => null,
        };

        if (type is { } eventType)
        {
            await EnqueueAsync(eventType, shift, cancellationToken).ConfigureAwait(false);
        }

        return Result<CashierShiftId>.Success(shift.Id);
    }

    /// <inheritdoc />
    public async Task<ShiftLocationFacts?> GetLocationFactsAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        var cached = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId)
            .Select(l => new { l.Kind, l.SettingsJson })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Settings the feed has not carried read as the strictest configuration,
        // never as a permissive default: a register must not become more
        // permissive by losing its connection.
        return cached is null
            ? null
            : new ShiftLocationFacts(cached.Kind, LocationSettings.FromJson(cached.SettingsJson));
    }

    /// <inheritdoc />
    public async Task<ShiftDeviceFacts?> GetDeviceFactsAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        DeviceStoreProfile enrolled = await profile.GetAsync(cancellationToken).ConfigureAwait(false);

        // A device answers for itself and nothing else. Any other identity is
        // unknown here, which is what the handler needs to hear.
        return enrolled.DeviceId == deviceId ? new ShiftDeviceFacts(enrolled.ShortCode) : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cash refunds are counted from the device's own refund rows, which is what
    /// stops a drawer that paid one out from looking short by exactly the amount
    /// refunded. Petty-cash payouts do not exist yet on either side.
    /// </remarks>
    public async Task<ShiftCashTotals> GetShiftCashTotalsAsync(
        CashierShiftId shiftId,
        CancellationToken cancellationToken)
    {
        decimal cashSales = await (
            from sale in context.LocalSales.AsNoTracking()
            join payment in context.Set<Payment>().AsNoTracking()
                on sale.Id equals EF.Property<SaleId>(payment, "SaleId")
            where sale.CashierShiftId == shiftId && payment.Method == PaymentMethod.Cash
            select payment.Amount)
            .SumAsync(cancellationToken)
            .ConfigureAwait(false);

        decimal cashRefunds = await context.LocalRefunds
            .AsNoTracking()
            .Where(r => r.CashierShiftId == shiftId && r.Method == PaymentMethod.Cash)
            .Select(r => r.Amount)
            .SumAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ShiftCashTotals(cashSales, cashRefunds, Payouts: 0m);
    }

    /// <inheritdoc />
    /// <remarks>
    /// This is what stops a sale being refunded past what it was paid: a device
    /// holding only some of a sale's returns would under-count and let the
    /// second refund through, so it answers from its own rows and the server
    /// re-checks the same rule when the events arrive.
    /// </remarks>
    public async Task<IReadOnlyDictionary<PaymentMethod, decimal>> GetRefundedAmountsByMethodAsync(
        SaleId saleId,
        CancellationToken cancellationToken,
        SalesReturnId? excludedReturnId = null)
    {
        var rows = await (
            from salesReturn in context.LocalSalesReturns.AsNoTracking()
            join refund in context.LocalRefunds.AsNoTracking()
                on salesReturn.Id equals refund.SalesReturnId
            where salesReturn.SaleId == saleId
                  && (excludedReturnId == null || salesReturn.Id != excludedReturnId)
            select new { refund.Method, refund.Amount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.Method)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ShiftForceCloseCandidate>> GetForceCloseCandidatesAsync(
        CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetForceCloseCandidatesAsync));

    /// <summary>
    /// Queues the business event in the same unit of work as the rows it
    /// describes, so an event never outlives a shift that rolled back.
    /// </summary>
    private Task<OutboxEvent> EnqueueAsync(
        SyncEventType type,
        CashierShift shift,
        CancellationToken cancellationToken)
        => outbox.EnqueueAsync(
            type,
            new ShiftSyncPayload(
                shift.Id.Value,
                shift.Number,
                shift.LocationId.Value,
                shift.DeviceId.Value,
                shift.CashierUserId.Value,
                shift.OpeningFloat,
                shift.BusinessDate,
                shift.OpenedAtUtc,
                shift.Status.ToString(),
                shift.ClosedAtUtc,
                shift.DeclaredCash,
                shift.CountedCash,
                shift.CashVariance,
                shift.IsForceClosed),
            shift.LocationId,
            cancellationToken);

    private static NotSupportedException NotOnADeviceYet(string member)
        => new(FormattableString.Invariant(
            $"{member} reads local sales, which a device does not carry yet. The use cases that need it are not registered on a device; reaching this is a registration mistake, not a runtime condition."));
}

/// <summary>
/// What the server is told about a shift that happened offline. It is the
/// business event, not the row: the server replays it through the same use case
/// it would have run online.
/// </summary>
/// <param name="ShiftId">The shift the device created.</param>
/// <param name="Number">The device-scoped SHF number printed at the till.</param>
/// <param name="LocationId">Where the shift was opened.</param>
/// <param name="DeviceId">The register.</param>
/// <param name="CashierUserId">The cashier.</param>
/// <param name="OpeningFloat">The float counted into the drawer.</param>
/// <param name="BusinessDate">The business date in the location's timezone.</param>
/// <param name="OpenedAtUtc">The device clock when the drawer opened.</param>
/// <param name="Status">The shift's status after the event.</param>
/// <param name="ClosedAtUtc">When the drawer was closed, for a close.</param>
/// <param name="DeclaredCash">What the cashier said was in the drawer.</param>
/// <param name="CountedCash">What was actually counted.</param>
/// <param name="CashVariance">
/// The variance the device computed from its own sales and refunds, and printed
/// on the cashier's Z-report. The server derives its own from the events it
/// accepted and that one stands; this is sent so a disagreement can be shown
/// rather than merely discovered.
/// </param>
/// <param name="IsForceClosed">Whether a worker closed it rather than a cashier.</param>
public sealed record ShiftSyncPayload(
    Guid ShiftId,
    string Number,
    Guid LocationId,
    Guid DeviceId,
    Guid CashierUserId,
    decimal OpeningFloat,
    DateOnly BusinessDate,
    DateTimeOffset OpenedAtUtc,
    string Status,
    DateTimeOffset? ClosedAtUtc = null,
    decimal? DeclaredCash = null,
    decimal? CountedCash = null,
    decimal? CashVariance = null,
    bool IsForceClosed = false);
