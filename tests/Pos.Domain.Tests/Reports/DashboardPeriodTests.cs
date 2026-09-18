using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Reports;

namespace Pos.Domain.Tests.Reports;

/// <summary>
/// Turning a named range into two dates. Every one of these is an off-by-one
/// somebody would otherwise find in a comparison and quietly distrust.
/// </summary>
public sealed class DashboardPeriodTests
{
    private static readonly DateOnly Today = new(2026, 9, 18);

    [Theory]
    [InlineData(DashboardRange.Today, "2026-09-18", "2026-09-18")]
    [InlineData(DashboardRange.Yesterday, "2026-09-17", "2026-09-17")]
    [InlineData(DashboardRange.Last7, "2026-09-12", "2026-09-18")]
    [InlineData(DashboardRange.Last30, "2026-08-20", "2026-09-18")]
    [InlineData(DashboardRange.Month, "2026-09-01", "2026-09-18")]
    [InlineData(DashboardRange.PrevMonth, "2026-08-01", "2026-08-31")]
    [InlineData(DashboardRange.Quarter, "2026-07-01", "2026-09-18")]
    [InlineData(DashboardRange.Year, "2026-01-01", "2026-09-18")]
    public void ANamedRangeResolvesToItsDates(DashboardRange range, string from, string to)
    {
        Result<(DateOnly From, DateOnly To)> resolved = DashboardPeriod.Resolve(range, Today, null, null);

        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.From.Should().Be(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture));
        resolved.Value.To.Should().Be(DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void LastSevenIsAWeek_NotEightDays()
    {
        Result<(DateOnly From, DateOnly To)> week = DashboardPeriod.Resolve(
            DashboardRange.Last7, Today, null, null);

        // Inclusive at both ends, so seven days means seven.
        (week.Value.To.DayNumber - week.Value.From.DayNumber + 1).Should().Be(7);
    }

    [Fact]
    public void ThePreviousMonthEndsOnItsLastDay()
    {
        Result<(DateOnly From, DateOnly To)> february = DashboardPeriod.Resolve(
            DashboardRange.PrevMonth, new DateOnly(2028, 3, 15), null, null);

        // A leap February, which is where a month-length assumption shows up.
        february.Value.From.Should().Be(new DateOnly(2028, 2, 1));
        february.Value.To.Should().Be(new DateOnly(2028, 2, 29));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 4)]
    [InlineData(9, 7)]
    [InlineData(12, 10)]
    public void AQuarterStartsOnItsOwnFirstMonth(int month, int expectedStart)
    {
        Result<(DateOnly From, DateOnly To)> quarter = DashboardPeriod.Resolve(
            DashboardRange.Quarter, new DateOnly(2026, month, 15), null, null);

        quarter.Value.From.Should().Be(new DateOnly(2026, expectedStart, 1));
    }

    [Fact]
    public void ACustomRangeNeedsBothItsDates()
    {
        // Falling back to today would show a number that looks like an answer to
        // the question that was asked.
        DashboardPeriod.Resolve(DashboardRange.Custom, Today, null, null)
            .Error!.Code.Should().Be("dashboard.custom_range_incomplete");

        DashboardPeriod.Resolve(DashboardRange.Custom, Today, Today, null)
            .Error!.Code.Should().Be("dashboard.custom_range_incomplete");
    }

    [Fact]
    public void ACustomRangeCannotRunBackwards()
        => DashboardPeriod.Resolve(DashboardRange.Custom, Today, Today, Today.AddDays(-1))
            .Error!.Code.Should().Be("dashboard.range_backwards");

    [Fact]
    public void ACustomRangeOfOneDayIsAllowed()
    {
        Result<(DateOnly From, DateOnly To)> single = DashboardPeriod.Resolve(
            DashboardRange.Custom, Today, Today, Today);

        single.IsSuccess.Should().BeTrue();
        single.Value.Should().Be((Today, Today));
    }

    [Fact]
    public void ARangeThisDashboardDoesNotKnowIsRefused()
        => DashboardPeriod.Resolve((DashboardRange)99, Today, null, null)
            .Error!.Code.Should().Be("dashboard.range_unknown");

    [Fact]
    public void TheDateGivenIsTheLocalOne_NotUtc()
    {
        // The resolver takes a business date rather than an instant, which is the
        // whole guarantee: "today" in Manila is a different day from "today" in
        // UTC for eight hours of every day, and the caller resolves that before
        // asking.
        Result<(DateOnly From, DateOnly To)> manila = DashboardPeriod.Resolve(
            DashboardRange.Today, new DateOnly(2026, 9, 19), null, null);

        manila.Value.From.Should().Be(new DateOnly(2026, 9, 19));
    }
}
