using Pos.Domain.Common;

namespace Pos.Domain.Reports;

/// <summary>
/// Turns a named range into the two business dates it means.
/// </summary>
/// <remarks>
/// <para>
/// Every range is resolved against a <b>timezone</b>, because "today" is a local
/// idea. A business in Manila asking for today's takings at nine in the morning
/// local time is eight hours into its trading day and none of the way into the
/// UTC one; a dashboard that answered in UTC would show a store a fraction of
/// what it had actually sold, which is the kind of wrong that gets explained away
/// rather than reported.
/// </para>
/// <para>
/// The boundaries are inclusive at both ends, matching how the sales reports read
/// a business date. A period that excluded its last day would quietly lose the
/// busiest one every time somebody asked about a month.
/// </para>
/// </remarks>
public static class DashboardPeriod
{
    /// <summary>Resolves a named range.</summary>
    /// <param name="range">The range.</param>
    /// <param name="today">The current business date in the relevant timezone.</param>
    /// <param name="from">The first date, for <see cref="DashboardRange.Custom"/>.</param>
    /// <param name="to">The last date, for <see cref="DashboardRange.Custom"/>.</param>
    /// <returns>The dates, or why they could not be resolved.</returns>
    public static Result<(DateOnly From, DateOnly To)> Resolve(
        DashboardRange range,
        DateOnly today,
        DateOnly? from,
        DateOnly? to)
    {
        if (range == DashboardRange.Custom)
        {
            // A custom range with no dates is a request nobody can answer. Falling
            // back to "today" would show a number that looks like an answer to the
            // question that was asked.
            if (from is not { } start || to is not { } end)
            {
                return Result<(DateOnly, DateOnly)>.Failure(DashboardErrors.CustomRangeIncomplete);
            }

            return end < start
                ? Result<(DateOnly, DateOnly)>.Failure(DashboardErrors.RangeBackwards)
                : Result<(DateOnly, DateOnly)>.Success((start, end));
        }

        if (!Enum.IsDefined(range))
        {
            return Result<(DateOnly, DateOnly)>.Failure(DashboardErrors.RangeUnknown);
        }

        DateOnly first = new(today.Year, today.Month, 1);

        return Result<(DateOnly, DateOnly)>.Success(range switch
        {
            DashboardRange.Today => (today, today),
            DashboardRange.Yesterday => (today.AddDays(-1), today.AddDays(-1)),

            // Seven days including today, so "last 7" is a week rather than eight
            // days. The off-by-one here is the one people notice in a comparison.
            DashboardRange.Last7 => (today.AddDays(-6), today),
            DashboardRange.Last30 => (today.AddDays(-29), today),
            DashboardRange.Month => (first, today),
            DashboardRange.PrevMonth => (first.AddMonths(-1), first.AddDays(-1)),
            DashboardRange.Quarter => (new DateOnly(today.Year, (((today.Month - 1) / 3) * 3) + 1, 1), today),
            _ => (new DateOnly(today.Year, 1, 1), today),
        });
    }
}

/// <summary>Why a dashboard request could not be answered.</summary>
public static class DashboardErrors
{
    /// <summary>A custom range arrived without both its dates.</summary>
    public static Error CustomRangeIncomplete => Error.Validation(
        "dashboard.custom_range_incomplete",
        "A custom range needs both a start date and an end date.");

    /// <summary>The range ends before it starts.</summary>
    public static Error RangeBackwards => Error.Validation(
        "dashboard.range_backwards",
        "A range must start on or before it ends.");

    /// <summary>The range is not one this dashboard knows.</summary>
    public static Error RangeUnknown => Error.Validation(
        "dashboard.range_unknown",
        "That is not a range this dashboard understands.");
}
