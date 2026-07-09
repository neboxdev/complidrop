using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// Property + edge tests for the pure, DATE-ONLY cadence arithmetic (SCHEMA §5): month-length clamps
/// (Jan 31 + 1mo ⇒ Feb 28/29), leap years (Feb 29 + 12mo), fixed-annual next-occurrence, and the grace
/// boundary. Because everything is <see cref="DateOnly"/> there is no time zone and DST cannot shift a
/// result — asserted explicitly across a US DST-transition date.
/// </summary>
public class CadenceCalculatorTests
{
    // ---------------- AddMonthsClamped: month-length clamps + leap years ----------------

    [Theory]
    [InlineData("2026-01-31", 1, "2026-02-28")]  // Jan 31 + 1mo -> Feb 28 (common year clamp)
    [InlineData("2024-01-31", 1, "2024-02-29")]  // Jan 31 + 1mo -> Feb 29 (leap year clamp)
    [InlineData("2024-02-29", 12, "2025-02-28")] // Feb 29 + 12mo -> Feb 28 (leap -> common)
    [InlineData("2024-02-29", 48, "2028-02-29")] // Feb 29 + 48mo -> Feb 29 (leap -> leap)
    [InlineData("2026-01-31", 13, "2027-02-28")] // crosses a year, still clamps
    [InlineData("2026-03-31", 1, "2026-04-30")]  // Mar 31 + 1mo -> Apr 30
    [InlineData("2026-12-31", 2, "2027-02-28")]  // Dec 31 + 2mo -> Feb 28 next year
    [InlineData("2026-10-31", 4, "2027-02-28")]  // Oct 31 + 4mo -> Feb 28
    [InlineData("2026-01-15", 0, "2026-01-15")]  // +0 is identity
    [InlineData("2026-01-31", 24, "2028-01-31")] // exact multiple of 12 preserves the day
    public void AddMonthsClamped_handles_month_length_and_leap_year_edges(string start, int months, string expected)
    {
        CadenceCalculator.AddMonthsClamped(DateOnly.Parse(start), months)
            .Should().Be(DateOnly.Parse(expected));
    }

    [Fact]
    public void AddMonthsClamped_is_a_property_valid_date_day_never_grows_month_advances_correctly()
    {
        // Exhaustive-ish property sweep: for a spread of start dates (including month-ends) and 0..36
        // months, the result is always a valid date, its day never exceeds the start day, and the month
        // advances by exactly `months` (mod 12).
        var starts = new[]
        {
            new DateOnly(2024, 1, 31), new DateOnly(2024, 2, 29), new DateOnly(2025, 2, 28),
            new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30), new DateOnly(2026, 11, 30),
            new DateOnly(2026, 12, 31), new DateOnly(2027, 6, 15),
        };

        foreach (var start in starts)
            foreach (var months in Enumerable.Range(0, 37))
            {
                var result = CadenceCalculator.AddMonthsClamped(start, months);

                result.Day.Should().BeLessThanOrEqualTo(start.Day, $"{start:O} + {months}mo");
                var expectedMonth = ((start.Month - 1 + months) % 12) + 1;
                result.Month.Should().Be(expectedMonth, $"{start:O} + {months}mo month");
                // Round-trips to a valid date: constructing it back must not throw.
                var act = () => new DateOnly(result.Year, result.Month, result.Day);
                act.Should().NotThrow();
            }
    }

    // ---------------- NextAnnualOccurrence: fixed-annual, incl. Feb 29 ----------------

    [Theory]
    [InlineData("2026-01-01", 5, 15, "2026-05-15")] // before the date this year -> this year
    [InlineData("2026-05-15", 5, 15, "2026-05-15")] // on the date -> today (inclusive)
    [InlineData("2026-05-16", 5, 15, "2027-05-15")] // after the date -> next year
    [InlineData("2025-01-01", 2, 29, "2025-02-28")] // Feb 29 in a common year -> clamp to Feb 28
    [InlineData("2024-01-01", 2, 29, "2024-02-29")] // Feb 29 in a leap year -> Feb 29
    [InlineData("2024-03-01", 2, 29, "2025-02-28")] // past Feb in a leap year -> next year, clamped
    public void NextAnnualOccurrence_finds_the_next_calendar_date_with_feb29_clamp(string onOrAfter, int month, int day, string expected)
    {
        CadenceCalculator.NextAnnualOccurrence(DateOnly.Parse(onOrAfter), month, day)
            .Should().Be(DateOnly.Parse(expected));
    }

    // ---------------- ClassifyTiming: grace boundary ----------------

    private static readonly DateOnly Due = new(2026, 6, 1);

    [Theory]
    [InlineData("2026-04-30", CadenceTiming.NotYetDue)] // due - 32 days, before the 30-day window
    [InlineData("2026-05-01", CadenceTiming.NotYetDue)] // due - 31 days: the EXACT last day outside the window (UNVER-28)
    [InlineData("2026-05-02", CadenceTiming.DueSoon)]   // due - 30 days, window opens (inclusive)
    [InlineData("2026-06-01", CadenceTiming.DueSoon)]   // exactly due
    [InlineData("2026-06-11", CadenceTiming.DueSoon)]   // due + 10 = end of grace (inclusive)
    [InlineData("2026-06-12", CadenceTiming.Overdue)]   // one day past grace
    public void ClassifyTiming_places_the_evaluation_date_around_due_plus_grace(string evalDate, CadenceTiming expected)
    {
        CadenceCalculator.ClassifyTiming(Due, DateOnly.Parse(evalDate), gracePeriodDays: 10, expiringWindowDays: 30)
            .Should().Be(expected);
    }

    [Fact]
    public void ClassifyTiming_with_no_due_date_is_no_deadline()
    {
        CadenceCalculator.ClassifyTiming(null, Due, 0, 30).Should().Be(CadenceTiming.NoDeadline);
    }

    [Fact]
    public void ClassifyTiming_with_zero_grace_is_overdue_the_day_after_due()
    {
        CadenceCalculator.ClassifyTiming(Due, Due, 0, 30).Should().Be(CadenceTiming.DueSoon);
        CadenceCalculator.ClassifyTiming(Due, Due.AddDays(1), 0, 30).Should().Be(CadenceTiming.Overdue);
    }

    // ---------------- ComputeNextDueDate: anchor branches ----------------

    [Fact]
    public void ComputeNextDueDate_documentExpiration_uses_the_printed_expiry()
    {
        var cadence = new Cadence { Kind = CadenceKind.Renewal, Anchor = CadenceAnchor.DocumentExpiration, PeriodMonths = 24 };
        var expiry = new DateOnly(2027, 3, 4);

        CadenceCalculator.ComputeNextDueDate(cadence, documentExpiration: expiry, issueDate: null, new DateOnly(2026, 1, 1))
            .Should().Be(expiry);
    }

    [Fact]
    public void ComputeNextDueDate_documentExpiration_falls_back_to_issue_plus_period_when_no_printed_expiry()
    {
        var cadence = new Cadence { Kind = CadenceKind.Renewal, Anchor = CadenceAnchor.DocumentExpiration, PeriodMonths = 24 };

        CadenceCalculator.ComputeNextDueDate(cadence, documentExpiration: null, issueDate: new DateOnly(2026, 1, 31), new DateOnly(2026, 6, 1))
            .Should().Be(new DateOnly(2028, 1, 31));
    }

    [Fact]
    public void ComputeNextDueDate_issueDate_anchor_adds_the_period_with_clamp()
    {
        var cadence = new Cadence { Kind = CadenceKind.Renewal, Anchor = CadenceAnchor.IssueDate, PeriodMonths = 1 };

        CadenceCalculator.ComputeNextDueDate(cadence, documentExpiration: null, issueDate: new DateOnly(2026, 1, 31), new DateOnly(2026, 1, 31))
            .Should().Be(new DateOnly(2026, 2, 28));
    }

    [Fact]
    public void ComputeNextDueDate_fixedDate_anchor_returns_the_next_annual_occurrence()
    {
        var cadence = new Cadence { Kind = CadenceKind.FixedAnnual, Anchor = CadenceAnchor.FixedDate, FixedDate = new MonthDay(5, 15) };

        CadenceCalculator.ComputeNextDueDate(cadence, documentExpiration: null, issueDate: null, new DateOnly(2026, 6, 1))
            .Should().Be(new DateOnly(2027, 5, 15));
    }

    [Fact]
    public void ComputeNextDueDate_returns_null_when_the_anchor_data_is_missing()
    {
        var issueAnchorNoIssue = new Cadence { Kind = CadenceKind.Renewal, Anchor = CadenceAnchor.IssueDate, PeriodMonths = 12 };
        CadenceCalculator.ComputeNextDueDate(issueAnchorNoIssue, null, null, new DateOnly(2026, 1, 1)).Should().BeNull();

        var oneTime = new Cadence { Kind = CadenceKind.OneTime, Anchor = CadenceAnchor.DocumentExpiration };
        CadenceCalculator.ComputeNextDueDate(oneTime, null, null, new DateOnly(2026, 1, 1)).Should().BeNull();
    }

    // ---------------- date-only purity / DST irrelevance ----------------

    [Fact]
    public void Arithmetic_is_date_only_and_unaffected_by_dst_transitions()
    {
        // 2026-03-08 is the US spring-forward date. DateOnly carries no time or zone, so a computation that
        // lands on that day yields exactly that calendar date — no off-by-one from a "2:00 AM doesn't exist"
        // instant. (If this were DateTime-in-a-zone arithmetic, that is precisely where a bug would hide.)
        CadenceCalculator.AddMonthsClamped(new DateOnly(2026, 2, 8), 1).Should().Be(new DateOnly(2026, 3, 8));
        CadenceCalculator.NextAnnualOccurrence(new DateOnly(2026, 1, 1), 3, 8).Should().Be(new DateOnly(2026, 3, 8));

        // And the fall-back date (2026-11-01) is equally plain.
        CadenceCalculator.NextAnnualOccurrence(new DateOnly(2026, 1, 1), 11, 1).Should().Be(new DateOnly(2026, 11, 1));
    }
}
