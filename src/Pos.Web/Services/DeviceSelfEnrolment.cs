using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Pos.Web.Services;

/// <summary>Settings for enrolling a browser without an administrator-issued code.</summary>
public sealed class DeviceSelfEnrolmentOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "DeviceEnrolment";

    /// <summary>
    /// Gets or sets whether a device manager's sign-in enrols the browser without
    /// asking. Off by default; the public demo turns it on.
    /// </summary>
    public bool AutoEnrol { get; set; }
}

/// <summary>
/// Lets someone who may manage devices enrol the browser they just signed in on.
/// </summary>
/// <remarks>
/// An enrolment code proves an administrator approved the device. When the
/// person signing in is that administrator, their session already proves it, so
/// this registers the device with their token and hands the code straight to the
/// browser's normal enrolment step. The device is still listed, bound to a
/// location, and revocable like any other; only the copying is gone.
/// </remarks>
/// <param name="api">The API client, carrying the signed-in session.</param>
/// <param name="session">The signed-in session.</param>
/// <param name="options">Self-enrolment settings.</param>
public sealed class DeviceSelfEnrolment(
    VaultFlowApiClient api,
    UserSession session,
    IOptions<DeviceSelfEnrolmentOptions> options)
{
    private const string DeviceManagePermission = "device.manage";
    private const int PwaPlatform = 4;

    // No 0/O or 1/I: short codes are printed on offline receipts.
    private const string ShortCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Gets whether the signed-in account may enrol this browser itself.</summary>
    public bool CanEnrolThisDevice => session.HasPermission(DeviceManagePermission);

    /// <summary>Gets whether to enrol without asking when <see cref="CanEnrolThisDevice"/> holds.</summary>
    public bool AutoEnrol => options.Value.AutoEnrol && CanEnrolThisDevice;

    /// <summary>Registers this browser as a device at a location and returns its one-time enrolment code.</summary>
    /// <param name="locationId">The location the device will belong to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The enrolment code, or a failure the screen can show.</returns>
    public async Task<ApiResult<string>> IssueCodeAsync(Guid locationId, CancellationToken cancellationToken)
    {
        if (!CanEnrolThisDevice)
        {
            return ApiResult<string>.Failure("Your account cannot set up devices. Ask an administrator for an enrollment code.");
        }

        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"Browser · {session.Current?.User.DisplayName ?? "web"} · {DateTime.UtcNow:yyyy-MM-dd}");

        // Short codes are unique; a random clash is unlikely but cheap to retry.
        for (int attempt = 0; ; attempt++)
        {
            string shortCode = "W" + RandomNumberGenerator.GetString(ShortCodeAlphabet, 5);
            ApiResult<PosDeviceRegistration> registered = await api.RegisterDeviceAsync(
                new PosRegisterDeviceRequest(shortCode, name, locationId, PwaPlatform),
                cancellationToken).ConfigureAwait(false);

            if (registered.ErrorCode == "device.short_code_taken" && attempt < 3)
            {
                continue;
            }

            if (!registered.IsSuccess || string.IsNullOrWhiteSpace(registered.Value?.EnrolmentCode))
            {
                return ApiResult<string>.Failure(
                    registered.Error ?? "The device could not be registered. Try again.", registered.ErrorCode);
            }

            return ApiResult<string>.Success(registered.Value.EnrolmentCode);
        }
    }
}
