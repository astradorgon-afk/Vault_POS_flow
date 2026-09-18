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

    /// <summary>The caller may report, but on nothing.</summary>
    /// <remarks>
    /// Told apart from an empty report on purpose. A manager assigned to no store
    /// who reads zeroes concludes the business sold nothing, which is a worse
    /// answer than being told the question does not apply to them.
    /// </remarks>
    /// <returns>The error.</returns>
    public static Error NoScope() => Error.Forbidden(
        "report.no_scope",
        "You are not assigned to any location, so there is nothing to report on.");

    /// <summary>The period runs backwards, or is longer than a report may cover.</summary>
    /// <param name="maxDays">The longest period allowed.</param>
    /// <returns>The error.</returns>
    public static Error PeriodInvalid(int maxDays) => Error.Validation(
        "report.period_invalid",
        FormattableString.Invariant(
            $"A report period must start on or before it ends and cover at most {maxDays} days."));
}