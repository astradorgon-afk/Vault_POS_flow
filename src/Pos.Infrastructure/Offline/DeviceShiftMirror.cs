using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Offline;

/// <summary>The shift a register is trading under, as the register knows it.</summary>
/// <param name="ShiftId">The shift.</param>
/// <param name="Number">Its SHF number.</param>
/// <param name="CashierUserId">Who opened it; only they may sell into it.</param>
/// <param name="CashierName">Their display name.</param>
/// <param name="BusinessDate">The business date it opened on.</param>
/// <param name="OpeningFloat">The cash counted into the drawer.</param>
/// <param name="OpenedAtUtc">When it opened.</param>
public sealed record DeviceKnownShift(
    Guid ShiftId,
    string Number,
    Guid CashierUserId,
    string CashierName,
    DateOnly BusinessDate,
    decimal OpeningFloat,
    DateTimeOffset OpenedAtUtc);

/// <summary>What a till needs before the first scan, answered from the device alone.</summary>
/// <param name="BusinessDate">Today in the store's time zone, by the device clock.</param>
/// <param name="CashRoundingIncrement">The store's cash rounding increment.</param>
/// <param name="Currency">The store's currency.</param>
/// <param name="OpenShift">The shift open on this register, if any.</param>
public sealed record DeviceTillContext(
    DateOnly BusinessDate,
    decimal CashRoundingIncrement,
    string Currency,
    DeviceKnownShift? OpenShift);

/// <summary>
/// Keeps the register's own shift table in step with head office, so that when
/// the connection drops the till still knows which drawer is open and whose it is.
/// </summary>
/// <remarks>
/// <para>
/// A register learns about shifts two ways: head office tells it which shift is
/// open on it, and it opens one itself while offline. Both end up as rows in
/// <c>local_cashier_shift</c>, because that is what the device's open-shift
/// check reads — a register that forgot the drawer head office opened would
/// happily open a second one offline, and head office would refuse it on sync.
/// </para>
/// <para>
/// A mirrored row is written directly, not through the shift repository, so it
/// queues no outbox event: head office already holds that shift. Rows head
/// office no longer reports as open are dropped, but only once every event the
/// device holds has been delivered; until then head office's answer is behind
/// the device's and must not overrule it.
/// </para>
/// </remarks>
/// <param name="database">The encrypted device store.</param>
/// <param name="clock">The device clock.</param>
public sealed class DeviceShiftMirror(DeviceDatabaseInitializer database, ISystemClock clock)
{
    /// <summary>Aligns the device's open shifts with what head office just reported.</summary>
    /// <param name="headOfficeOpenShift">The shift head office says is open on this register, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the device agrees with head office.</returns>
    public async Task ReconcileAsync(DeviceKnownShift? headOfficeOpenShift, CancellationToken cancellationToken = default)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeviceStoreProfile? profile = await context.DeviceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return;
        }

        bool behind = await context.Outbox
            .AsNoTracking()
            .AnyAsync(OutboxEvent.IsUnsent, cancellationToken)
            .ConfigureAwait(false);

        List<CashierShift> open = await context.LocalShifts
            .Where(s => s.DeviceId == profile.DeviceId
                        && (s.Status == ShiftStatus.Open || s.Status == ShiftStatus.Suspended))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!behind)
        {
            foreach (CashierShift stale in open.Where(s => s.Id.Value != headOfficeOpenShift?.ShiftId))
            {
                context.LocalShifts.Remove(stale);
            }
        }

        if (headOfficeOpenShift is { } known
            && open.All(s => s.Id.Value != known.ShiftId)
            && (!behind || open.Count == 0))
        {
            Result<DocumentNumber> number = DocumentNumber.Parse(known.Number);
            if (number.IsSuccess)
            {
                Result<CashierShift> mirrored = CashierShift.Open(
                    number.Value,
                    profile.LocationId,
                    profile.DeviceId,
                    new UserId(known.CashierUserId),
                    known.OpeningFloat,
                    known.BusinessDate,
                    known.OpenedAtUtc,
                    new CashierShiftId(known.ShiftId));

                if (mirrored.IsSuccess)
                {
                    context.LocalShifts.Add(mirrored.Value);
                }
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers the till's opening questions without head office.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The till context, or null when the register has no store data yet.</returns>
    public async Task<DeviceTillContext?> GetTillContextAsync(CancellationToken cancellationToken = default)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeviceStoreProfile? profile = await context.DeviceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        DeviceCachedLocation? store = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == profile.LocationId, cancellationToken)
            .ConfigureAwait(false);
        if (store is null)
        {
            return null;
        }

        // Missing settings read as the strictest configuration, never a guess.
        LocationSettings settings = LocationSettings.FromJson(store.SettingsJson);

        CashierShift? shift = await context.LocalShifts
            .AsNoTracking()
            .Where(s => s.DeviceId == profile.DeviceId && s.Status == ShiftStatus.Open)
            .OrderByDescending(s => s.OpenedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        DeviceKnownShift? known = null;
        if (shift is not null)
        {
            string? cashier = await context.Users
                .AsNoTracking()
                .Where(u => u.Id == shift.CashierUserId)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            cashier ??= await context.OfflineCredentials
                .AsNoTracking()
                .Where(c => c.UserId == shift.CashierUserId)
                .Select(c => c.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            known = new DeviceKnownShift(
                shift.Id.Value,
                shift.Number,
                shift.CashierUserId.Value,
                cashier ?? "another cashier",
                shift.BusinessDate,
                shift.OpeningFloat,
                shift.OpenedAtUtc);
        }

        return new DeviceTillContext(
            clock.BusinessDateFor(store.TimeZoneId),
            settings.CashRoundingIncrement,
            store.CurrencyCode,
            known);
    }
}
