using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>An active browser register a cashier may pick up.</summary>
/// <param name="Id">The device identifier, sent back as <c>X-Device-Id</c>.</param>
/// <param name="ShortCode">The short code embedded in its document numbers.</param>
/// <param name="Name">The register's display name.</param>
public sealed record RegisterSummary(Guid Id, string ShortCode, string Name);

/// <summary>The shift currently open on a register, if any.</summary>
/// <param name="ShiftId">The shift identifier.</param>
/// <param name="Number">Its SHF document number.</param>
/// <param name="CashierId">Who opened it.</param>
/// <param name="CashierName">The cashier's display name.</param>
/// <param name="BusinessDate">The business date it opened on.</param>
/// <param name="OpenedAtUtc">When it opened.</param>
/// <param name="OpeningFloat">The drawer float at opening.</param>
public sealed record OpenShiftSummary(
    Guid ShiftId,
    string Number,
    Guid CashierId,
    string CashierName,
    DateOnly BusinessDate,
    DateTimeOffset OpenedAtUtc,
    decimal OpeningFloat);

/// <summary>Everything a checkout needs to know before the first scan.</summary>
/// <param name="DeviceId">The calling device.</param>
/// <param name="DeviceShortCode">Its short code.</param>
/// <param name="DeviceName">Its display name.</param>
/// <param name="BusinessDate">The store's current business date, in its own timezone.</param>
/// <param name="VatRate">The location's VAT rate as a fraction of the gross price.</param>
/// <param name="CashRoundingIncrement">The increment cash change rounds to.</param>
/// <param name="OpenShift">The shift open on this register, or <see langword="null"/>.</param>
public sealed record TerminalSession(
    Guid DeviceId,
    string DeviceShortCode,
    string DeviceName,
    DateOnly BusinessDate,
    decimal VatRate,
    decimal CashRoundingIncrement,
    OpenShiftSummary? OpenShift);

/// <summary>The body of a number allocation.</summary>
/// <param name="DocumentType">One of <c>SAL</c>, <c>RET</c> or <c>SHF</c>.</param>
public sealed record NextNumberRequest(string DocumentType);

/// <summary>The allocated number.</summary>
/// <param name="DocumentType">The prefix of the allocated type (<c>SAL</c>, <c>RET</c> or <c>SHF</c>).</param>
/// <param name="Number">The human-readable number, for example <c>SAL-2026-W02-000123</c>.</param>
public sealed record NextNumberResponse(string DocumentType, string Number);

/// <summary>
/// The checkout orchestration endpoints used by browser registers
/// (docs/POS.md §2). A web terminal is an ordinary device of platform
/// <see cref="DevicePlatform.Web"/>, activated on registration because the
/// cashier's logged-in session is its credential. It has no offline counter, so
/// the server allocates its device-scoped numbers from the shared counter table
/// keyed by its short code — never from a physical device's counter, which must
/// stay with the device (docs/DECISIONS.md, web-terminal note).
/// </summary>
public static class TerminalEndpoints
{
    /// <summary>Maps the terminal routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/terminal").WithTags("Terminal");

        group.MapGet("/registers", ListRegistersAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.Create)
            {
                Scope = ScopeSource.QueryValue,
            })
            .WithName("ListTerminalRegisters")
            .WithSummary("Lists the active browser registers a cashier may pick up at a store.");

        group.MapGet("/session", SessionAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.Create)
            {
                Scope = ScopeSource.QueryValue,
            })
            .WithName("GetTerminalSession")
            .WithSummary("Bootstraps the checkout: business date, pricing settings and open shift.");

        group.MapPost("/{locationId:guid}/next-number", NextNumberAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.Create)
            {
                Scope = ScopeSource.RouteValue,
            })
            .WithName("AllocateTerminalNumber")
            .WithSummary("Allocates the next SAL, RET or SHF number for the calling register.");

        return app;
    }

    private static async Task<IResult> ListRegistersAsync(
        [FromQuery] Guid locationId,
        PosDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        LocationId scope = new(locationId);

        List<RegisterSummary> registers = await context.Devices
            .AsNoTracking()
            .Where(device => device.LocationId == scope
                && device.Platform == DevicePlatform.Web
                && device.Status == DeviceStatus.Active)
            .OrderBy(device => device.Name)
            .Select(device => new RegisterSummary(device.Id.Value, device.ShortCode, device.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(registers);
    }

    private static async Task<IResult> SessionAsync(
        [FromQuery] Guid locationId,
        PosDbContext context,
        ICurrentUser currentUser,
        ISystemClock clock,
        IShiftRepository shifts,
        CancellationToken cancellationToken)
    {
        if (currentUser.DeviceId is not { } deviceId)
        {
            return Problem(
                Result.Failure(Error.Validation(
                    "device.required",
                    "This operation needs a register. Pick a register and try again.")),
                currentUser);
        }

        Result<Device> load = await LoadTerminalAsync(context, deviceId, new LocationId(locationId), cancellationToken)
            .ConfigureAwait(false);

        if (load.IsFailure)
        {
            return Problem(Result.Failure(load.Errors), currentUser);
        }

        Device terminal = load.Value;

        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == new LocationId(locationId), cancellationToken)
            .ConfigureAwait(false);

        if (location is null || !location.IsActive || !location.IsPhysical)
        {
            return Problem(
                Result.Failure(Error.NotFound(
                    "location.not_found", "The store does not exist or is not active.")),
                currentUser);
        }

        DateOnly businessDate = clock.BusinessDateFor(location.TimeZoneId);

        CashierShift? open = await shifts
            .GetOpenShiftForDeviceAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);

        OpenShiftSummary? openShift = null;

        if (open is not null)
        {
            string cashierName = await context.Users
                .AsNoTracking()
                .Where(user => user.Id == open.CashierUserId.Value)
                .Select(user => user.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) ?? "Unknown cashier";

            openShift = new OpenShiftSummary(
                open.Id.Value,
                open.Number,
                open.CashierUserId.Value,
                cashierName,
                open.BusinessDate,
                open.OpenedAtUtc,
                open.OpeningFloat);
        }

        return TypedResults.Ok(new TerminalSession(
            terminal.Id.Value,
            terminal.ShortCode,
            terminal.Name,
            businessDate,
            location.Settings.VatRate,
            location.Settings.CashRoundingIncrement,
            openShift));
    }

    private static async Task<IResult> NextNumberAsync(
        Guid locationId,
        [FromBody] NextNumberRequest body,
        PosDbContext context,
        ICurrentUser currentUser,
        IPermissionEvaluator permissions,
        IDocumentNumberGenerator generator,
        ISystemClock clock,
        CancellationToken cancellationToken)
    {
        if (currentUser.DeviceId is not { } deviceId)
        {
            return Problem(
                Result.Failure(Error.Validation(
                    "device.required",
                    "This operation needs a register. Pick a register and try again.")),
                currentUser);
        }

        Result<Device> load = await LoadTerminalAsync(context, deviceId, new LocationId(locationId), cancellationToken)
            .ConfigureAwait(false);

        if (load.IsFailure)
        {
            return Problem(Result.Failure(load.Errors), currentUser);
        }

        Device terminal = load.Value;

        // Only browser terminals number their documents on the server. A
        // physical terminal's counter lives on the device, so a server-minted
        // number would collide with the numbers it issues offline.
        if (terminal.Platform != DevicePlatform.Web)
        {
            return Problem(
                Result.Failure(Error.Conflict(
                    "device.not_web",
                    "Only browser registers are numbered by the server; a physical device numbers its own documents.")),
                currentUser);
        }

        if (!TryParseDocumentType(body.DocumentType, out DocumentType type))
        {
            return Problem(
                Result.Failure(Error.Validation(
                    "terminal.document_type_invalid",
                    "Document type must be SAL, RET or SHF.")),
                currentUser);
        }

        // The route's sale.create check covers selling; allocating a shift
        // number additionally requires that this cashier may open shifts.
        if (type == DocumentType.CashierShift
            && !await permissions
                .HasPermissionAsync(
                    currentUser.UserId!.Value,
                    Permissions.Sales.OpenShift,
                    new LocationId(locationId),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return Problem(
                Result.Failure(Error.Forbidden(
                    "authorization.denied",
                    "Starting a shift needs the shift.open permission at this store.")),
                currentUser);
        }

        DocumentNumber number = await generator
            .NextScopedAsync(type, terminal.ShortCode, cancellationToken)
            .ConfigureAwait(false);

        terminal.RecordContact(clock.UtcNow);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new NextNumberResponse(
            DocumentNumber.PrefixFor(type),
            number.Value));
    }

    /// <summary>Loads an active device belonging to the requested location.</summary>
    /// <param name="context">The database context.</param>
    /// <param name="deviceId">The calling device.</param>
    /// <param name="locationId">The location its operation must belong to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracked device, or the reason it cannot operate here.</returns>
    private static async Task<Result<Device>> LoadTerminalAsync(
        PosDbContext context,
        DeviceId deviceId,
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        Device? device = await context.Devices
            .AsTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Result<Device>.Failure(Error.NotFound(
                "device.not_found", "The register does not exist."));
        }

        if (!device.IsOperational)
        {
            return Result<Device>.Failure(Error.Conflict(
                "device.not_active", "The register is not active. Ask an administrator."));
        }

        if (device.LocationId != locationId)
        {
            return Result<Device>.Failure(Error.Conflict(
                "device.wrong_location", "The register belongs to another store."));
        }

        return Result<Device>.Success(device);
    }

    private static bool TryParseDocumentType(string? raw, out DocumentType type)
    {
        type = raw?.Trim().ToUpperInvariant() switch
        {
            "SAL" => DocumentType.Sale,
            "RET" => DocumentType.SalesReturn,
            "SHF" => DocumentType.CashierShift,
            _ => default,
        };

        return type is DocumentType.Sale or DocumentType.SalesReturn or DocumentType.CashierShift;
    }

    private static IResult Problem(Result result, ICurrentUser currentUser)
        => ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
}