using Pos.Domain.Common;

namespace Pos.Domain.Organizations;

/// <summary>Errors raised by the location and organization aggregates.</summary>
public static class LocationErrors
{
    /// <summary>An attempt was made to close an already-closed location.</summary>
    public static Error AlreadyClosed { get; } = Error.Conflict(
        "location.already_closed",
        "This location is already closed.");

    /// <summary>A system external counterparty cannot be user-closed.</summary>
    public static Error CannotCloseSystemLocation { get; } = Error.Conflict(
        "location.cannot_close_system_location",
        "A system counterparty location cannot be closed by a user.");

    /// <summary>A closing was requested while stock or open documents remain.</summary>
    public static Error CannotCloseWithOpenWork { get; } = Error.Conflict(
        "location.cannot_close_with_open_work",
        "A location with outstanding stock or open documents cannot be closed.");

    /// <summary>Exactly one active main warehouse may exist.</summary>
    public static Error MultipleActiveMainWarehouses { get; } = Error.Conflict(
        "location.multiple_active_main_warehouses",
        "Only one Main Warehouse may be active at a time.");

    /// <summary>A location code is already in use within the organization.</summary>
    /// <param name="code">The duplicated code.</param>
    /// <returns>The error.</returns>
    public static Error DuplicateCode(string code) => Error.Conflict(
        "location.code_taken",
        FormattableString.Invariant($"The location code {code} is already in use."),
        new Dictionary<string, object?> { ["code"] = code });

    /// <summary>A referenced location does not exist.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error Unknown(LocationId id) => Error.NotFound(
        "location.unknown",
        FormattableString.Invariant($"Location {id.Value} was not found."));

    /// <summary>Settings on a system counterparty location were requested.</summary>
    public static Error CannotUpdateSystemLocationSettings { get; } = Error.Conflict(
        "location.cannot_update_system_settings",
        "A system counterparty location has no operational settings to change.");
}