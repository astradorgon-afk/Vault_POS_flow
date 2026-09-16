using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Identity;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Devices;

/// <summary>A newly registered device and the code needed to enrol it.</summary>
/// <param name="DeviceId">The device's identifier.</param>
/// <param name="ShortCode">The short code embedded in its offline document numbers.</param>
/// <param name="EnrolmentCode">
/// The one-time code, returned exactly once. It is stored only as a hash, so it
/// cannot be retrieved again; a lost code is reissued, not recovered. Web
/// terminals activate on registration and receive no code.
/// </param>
/// <param name="ExpiresAtUtc">When the code stops working, or <see langword="null"/> when no code was issued.</param>
public sealed record DeviceRegistration(
    DeviceId DeviceId,
    string ShortCode,
    string? EnrolmentCode,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>The details a device reports when it enrols.</summary>
/// <param name="EnrolmentCode">The one-time code.</param>
/// <param name="PublicKeyThumbprint">Thumbprint of the key the device generated locally.</param>
/// <param name="Platform">The platform it runs on.</param>
/// <param name="AppVersion">The application version.</param>
/// <param name="OsVersion">The operating system version.</param>
public sealed record DeviceEnrolmentRequest(
    string EnrolmentCode,
    string PublicKeyThumbprint,
    DevicePlatform Platform,
    string? AppVersion,
    string? OsVersion);

/// <summary>Registers, enrols and retires devices.</summary>
public interface IDeviceService
{
    /// <summary>Registers a device and issues its first enrolment code.</summary>
    /// <param name="shortCode">The short code for offline document numbers.</param>
    /// <param name="name">A human-readable name.</param>
    /// <param name="locationId">The location it belongs to.</param>
    /// <param name="platform">The platform it runs on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The registration, including the code, or the reason it was refused.</returns>
    Task<Result<DeviceRegistration>> RegisterAsync(
        string shortCode,
        string name,
        LocationId locationId,
        DevicePlatform platform,
        CancellationToken cancellationToken);

    /// <summary>Issues a fresh enrolment code for a device that has not enrolled.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new code, or the reason it was refused.</returns>
    Task<Result<DeviceRegistration>> ReissueEnrolmentCodeAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken);

    /// <summary>Completes enrolment against a one-time code.</summary>
    /// <param name="request">What the device reports.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The enrolled device's identifier, or the reason it was refused.</returns>
    Task<Result<DeviceId>> EnrolAsync(DeviceEnrolmentRequest request, CancellationToken cancellationToken);

    /// <summary>Temporarily blocks a device.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the reason the change was refused.</returns>
    Task<Result> SuspendAsync(DeviceId deviceId, string reason, CancellationToken cancellationToken);

    /// <summary>Lifts a suspension.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the reason the change was refused.</returns>
    Task<Result> ReactivateAsync(DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Permanently blocks a device and ends its sessions.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the reason the change was refused.</returns>
    Task<Result> RevokeAsync(DeviceId deviceId, string reason, CancellationToken cancellationToken);
}

/// <summary>The default device service.</summary>
/// <param name="context">The database context.</param>
/// <param name="tokens">The token service, which generates enrolment codes.</param>
/// <param name="clock">The authoritative clock.</param>
/// <param name="currentUser">The caller.</param>
/// <param name="options">Security settings.</param>
/// <param name="audit">The audit writer.</param>
public sealed class DeviceService(
    PosDbContext context,
    ITokenService tokens,
    ISystemClock clock,
    ICurrentUser currentUser,
    IOptions<SecurityOptions> options,
    IAuditWriter audit) : IDeviceService
{
    private readonly SecurityOptions _security = options.Value;

    /// <inheritdoc />
    public async Task<Result<DeviceRegistration>> RegisterAsync(
        string shortCode,
        string name,
        LocationId locationId,
        DevicePlatform platform,
        CancellationToken cancellationToken)
    {
        UserId actor = RequireActor();
        DateTimeOffset now = clock.UtcNow;

        string normalised = (shortCode ?? string.Empty).Trim().ToUpperInvariant();

        bool taken = await context.Devices
            .AnyAsync(d => d.ShortCode == normalised, cancellationToken)
            .ConfigureAwait(false);

        if (taken)
        {
            // Two devices sharing a short code would mint colliding receipt
            // numbers the moment either went offline.
            return Result<DeviceRegistration>.Failure(Error.Conflict(
                "device.short_code_taken",
                FormattableString.Invariant($"Short code {normalised} is already in use by another device.")));
        }

        Result<Device> device = Device.Register(normalised, name, locationId, platform, now, actor);

        if (device.IsFailure)
        {
            return Result<DeviceRegistration>.Failure(device.Errors);
        }

        context.Devices.Add(device.Value);

        // A browser terminal has no device key to bind at enrolment: the
        // cashier's login is the credential. It activates immediately, which is
        // also the moment its document numbers first matter, so no ceremony is
        // skipped — there is nothing a one-time code would add.
        if (!platform.RequiresEnrolmentCode())
        {
            Result activated = device.Value.ActivateForWeb(now);

            if (activated.IsFailure)
            {
                return Result<DeviceRegistration>.Failure(activated.Errors);
            }

            await audit.WriteAsync(
                new AuditEntry(
                    AuditActions.Devices.Enrolled,
                    nameof(Device),
                    device.Value.Id.Value,
                    LocationId: locationId),
                cancellationToken).ConfigureAwait(false);

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result<DeviceRegistration>.Success(new DeviceRegistration(
                device.Value.Id, normalised, EnrolmentCode: null, ExpiresAtUtc: null));
        }

        DeviceRegistration registration = await IssueCodeAsync(device.Value, actor, now, cancellationToken)
            .ConfigureAwait(false);

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Devices.EnrolmentCodeIssued,
                nameof(Device),
                device.Value.Id.Value,
                LocationId: locationId),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<DeviceRegistration>.Success(registration);
    }

    /// <inheritdoc />
    public async Task<Result<DeviceRegistration>> ReissueEnrolmentCodeAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        UserId actor = RequireActor();
        DateTimeOffset now = clock.UtcNow;

        Device? device = await context.Devices
            .AsTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Result<DeviceRegistration>.Failure(Error.NotFound(
                "device.not_found", "No such device."));
        }

        if (device.Status != DeviceStatus.PendingEnrolment)
        {
            return Result<DeviceRegistration>.Failure(Error.Conflict(
                "device.already_enrolled",
                "This device has already been enrolled. Revoke it and register a new one instead."));
        }

        // Outstanding codes are burned first: reissuing must not leave an older
        // code from a previous, possibly overheard, conversation still live.
        List<DeviceEnrolmentCode> outstanding = await context.DeviceEnrolmentCodes
            .AsTracking()
            .Where(c => c.DeviceId == deviceId && c.RedeemedAtUtc == null && !c.IsBurned)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (DeviceEnrolmentCode code in outstanding)
        {
            for (int i = 0; i < DeviceEnrolmentCode.MaxRedemptionAttempts; i++)
            {
                code.RecordFailedAttempt();
            }
        }

        DeviceRegistration registration = await IssueCodeAsync(device, actor, now, cancellationToken)
            .ConfigureAwait(false);

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Devices.EnrolmentCodeIssued,
                nameof(Device),
                device.Id.Value,
                LocationId: device.LocationId),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<DeviceRegistration>.Success(registration);
    }

    /// <inheritdoc />
    public async Task<Result<DeviceId>> EnrolAsync(
        DeviceEnrolmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = clock.UtcNow;
        byte[] hash = tokens.HashRefreshToken(request.EnrolmentCode);

        DeviceEnrolmentCode? code = await context.DeviceEnrolmentCodes
            .AsTracking()
            .FirstOrDefaultAsync(c => c.CodeHash == hash, cancellationToken)
            .ConfigureAwait(false);

        if (code is null)
        {
            return Result<DeviceId>.Failure(new Error(
                "device.enrolment_code_invalid",
                "This enrolment code is not valid. Ask an administrator for a new one.",
                ErrorType.Unauthenticated));
        }

        Result redeemed = code.Redeem(now);

        if (redeemed.IsFailure)
        {
            code.RecordFailedAttempt();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result<DeviceId>.Failure(redeemed.Error);
        }

        Device? device = await context.Devices
            .AsTracking()
            .FirstOrDefaultAsync(d => d.Id == code.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Result<DeviceId>.Failure(Error.NotFound("device.not_found", "No such device."));
        }

        Result enrolment = device.CompleteEnrolment(
            request.PublicKeyThumbprint, request.AppVersion, request.OsVersion, now);

        if (enrolment.IsFailure)
        {
            return Result<DeviceId>.Failure(enrolment.Errors);
        }

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Devices.Enrolled,
                nameof(Device),
                device.Id.Value,
                LocationId: device.LocationId),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<DeviceId>.Success(device.Id);
    }

    /// <inheritdoc />
    public async Task<Result> SuspendAsync(DeviceId deviceId, string reason, CancellationToken cancellationToken)
    {
        UserId actor = RequireActor();
        DateTimeOffset now = clock.UtcNow;

        Device? device = await context.Devices
            .AsTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Result.Failure(Error.NotFound("device.not_found", "No such device."));
        }

        Result suspended = device.Suspend(reason, now, actor);

        if (suspended.IsFailure)
        {
            return suspended;
        }

        await RevokeDeviceTokensAsync(deviceId, now, "device suspended", cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Devices.Suspended,
                nameof(Device),
                deviceId.Value,
                Reason: reason,
                LocationId: device.LocationId),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ReactivateAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        UserId actor = RequireActor();
        DateTimeOffset now = clock.UtcNow;

        Device? device = await context.Devices
            .AsTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Result.Failure(Error.NotFound("device.not_found", "No such device."));
        }

        Result reactivated = device.Reactivate(now, actor);

        if (reactivated.IsFailure)
        {
            return reactivated;
        }

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Devices.Reactivated,
                nameof(Device),
                deviceId.Value,
                LocationId: device.LocationId),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(DeviceId deviceId, string reason, CancellationToken cancellationToken)
    {
        UserId actor = RequireActor();
        DateTimeOffset now = clock.UtcNow;

        Device? device = await context.Devices
            .AsTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Result.Failure(Error.NotFound("device.not_found", "No such device."));
        }

        Result revoked = device.Revoke(reason, now, actor);

        if (revoked.IsFailure)
        {
            return revoked;
        }

        await RevokeDeviceTokensAsync(deviceId, now, "device revoked", cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Devices.Revoked,
                nameof(Device),
                deviceId.Value,
                Reason: reason,
                LocationId: device.LocationId),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    private async Task<DeviceRegistration> IssueCodeAsync(
        Device device,
        UserId actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RefreshTokenMaterial material = tokens.CreateEnrolmentCode();

        DeviceEnrolmentCode code = DeviceEnrolmentCode.Issue(
            device.Id, material.Hash, now, _security.DeviceEnrolmentCodeLifetime, actor);

        context.DeviceEnrolmentCodes.Add(code);

        await Task.CompletedTask.ConfigureAwait(false);

        return new DeviceRegistration(device.Id, device.ShortCode, material.Value, code.ExpiresAtUtc);
    }

    private async Task RevokeDeviceTokensAsync(
        DeviceId deviceId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        List<RefreshToken> live = await context.RefreshTokens
            .AsTracking()
            .Where(t => t.DeviceId == deviceId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (RefreshToken token in live)
        {
            token.Revoke(RefreshTokenRevocationReason.DeviceRevoked, now);
        }

        List<DeviceSession> sessions = await context.DeviceSessions
            .AsTracking()
            .Where(s => s.DeviceId == deviceId && s.EndedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (DeviceSession session in sessions)
        {
            session.End(now, reason);
        }
    }

    private UserId RequireActor()
        => currentUser.UserId
           ?? throw new InvalidOperationException("Device administration requires an authenticated user.");
}
