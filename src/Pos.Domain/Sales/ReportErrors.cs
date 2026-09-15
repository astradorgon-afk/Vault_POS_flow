using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// The expected business failures a report can produce. Codes are part of the
/// API contract; messages may change.
/// </summary>
public static class ReportErrors
{
    /// <summary>The requested location does not exist, so no report exists for it.</summary>
    /// <param name="id">The location identifier.</param>
    /// <returns>The error.</returns>
    public static Error LocationUnknown(LocationId id) => Error.NotFound(
        "report.location_unknown",
        FormattableString.Invariant($"No report exists for the location {id}."));

    /// <summary>The location is outside the caller's report scope.</summary>
    /// <param name="id">The location identifier.</param>
    /// <returns>The error.</returns>
    public static Error OutsideScope(LocationId id) => Error.Forbidden(
        "report.outside_scope",
        FormattableString.Invariant($"The location {id} is outside your report scope."));
}