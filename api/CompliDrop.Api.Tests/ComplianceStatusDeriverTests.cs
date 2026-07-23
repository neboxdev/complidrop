using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for the date-overlay that keeps compliance verdicts from going stale (#257) and the
/// future-effective demotion (#362 / ADR 0041). The deriver must mirror ComplianceCheckService's date
/// precedence exactly so a freshly-evaluated doc and a swept/derived doc never disagree. No DB, no clock —
/// today is passed in.
/// </summary>
public sealed class ComplianceStatusDeriverTests
{
    private static readonly DateTime Today = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Null_expiration_returns_stored_unchanged()
    {
        foreach (var stored in Enum.GetValues<ComplianceStatus>())
            ComplianceStatusDeriver.Effective(stored, null, null, Today).Should().Be(stored);
    }

    [Theory]
    [InlineData(ComplianceStatus.Compliant)]
    [InlineData(ComplianceStatus.NonCompliant)]
    [InlineData(ComplianceStatus.Pending)]
    [InlineData(ComplianceStatus.ExpiringSoon)]
    public void Past_expiration_is_Expired_regardless_of_stored_verdict(ComplianceStatus stored)
    {
        // Expired is top precedence — even a rules-failing doc reads Expired once the date passes,
        // matching the service's early-return expiry branch.
        ComplianceStatusDeriver.Effective(stored, Today.AddDays(-1), null, Today)
            .Should().Be(ComplianceStatus.Expired);
    }

    [Fact]
    public void Expiring_today_is_not_yet_expired()
    {
        // Strict `<` for Expired (mirrors the service): expiring exactly today is ExpiringSoon.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today, null, Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Theory]
    [InlineData(ComplianceStatus.Compliant)]
    [InlineData(ComplianceStatus.ExpiringSoon)]
    [InlineData(ComplianceStatus.Pending)]
    public void Within_window_overlays_ExpiringSoon_for_non_failing_verdicts(ComplianceStatus stored)
    {
        ComplianceStatusDeriver.Effective(stored, Today.AddDays(10), null, Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public void Within_window_keeps_a_hard_fail_NonCompliant()
    {
        // A failing doc is still failing even when expiring soon — the date doesn't soften the verdict.
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, Today.AddDays(10), null, Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void Exactly_30_days_is_within_the_window_but_31_is_not()
    {
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(30), null, Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(31), null, Today)
            .Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public void Far_future_expiration_returns_stored_unchanged()
    {
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(200), null, Today)
            .Should().Be(ComplianceStatus.Compliant);
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, Today.AddDays(200), null, Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void Time_component_of_today_is_ignored()
    {
        // A doc expiring today at any wall-clock time is still "today" — the overlay compares dates.
        var todayAfternoon = new DateTime(2026, 6, 15, 18, 30, 0, DateTimeKind.Utc);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today, null, todayAfternoon)
            .Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public void WindowUpperBoundExclusive_is_the_instant_equivalent_of_the_date_window()
    {
        // #294: the SQL read sites compare a raw timestamptz against this bound to reproduce the
        // deriver's inclusive date window (exp.Date <= today + N). It must be UTC midnight of day
        // N+1, independent of today's time component.
        var todayNoon = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        ComplianceStatusDeriver.WindowUpperBoundExclusive(todayNoon, 30)
            .Should().Be(new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc));

        var bound = ComplianceStatusDeriver.WindowUpperBoundExclusive(todayNoon, ComplianceStatusDeriver.ExpiringSoonWindowDays);

        // A time-bearing expiry ON the boundary day (day 30 at noon) is INSIDE the window (`< bound`),
        // matching the deriver's ExpiringSoon — where a naive `<= today+30` midnight bound excluded it.
        var onBoundaryNoon = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        (onBoundaryNoon < bound).Should().BeTrue();
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, onBoundaryNoon, null, todayNoon)
            .Should().Be(ComplianceStatus.ExpiringSoon);

        // The day after the window (day 31 at noon) is OUTSIDE (`>= bound`), matching stored Compliant.
        var pastBoundaryNoon = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        (pastBoundaryNoon < bound).Should().BeFalse();
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, pastBoundaryNoon, null, todayNoon)
            .Should().Be(ComplianceStatus.Compliant);
    }

    // ---- #362 / ADR 0041: future-effective (not-yet-in-force) demotion ----

    [Theory]
    [InlineData(ComplianceStatus.Compliant)]
    [InlineData(ComplianceStatus.ExpiringSoon)]
    public void Future_effective_demotes_an_affirmative_verdict_to_Pending(ComplianceStatus stored)
    {
        // A cert effective next month, expiring well beyond the 30-day window, all rules passed. It
        // provides no coverage IN FORCE today, so an affirmative verdict reads Pending ("not yet in force").
        ComplianceStatusDeriver.Effective(stored, Today.AddDays(300), Today.AddDays(30), Today)
            .Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public void Future_effective_within_the_expiring_window_still_reads_Pending_not_ExpiringSoon()
    {
        // A short future-effective policy (effective in 5 days, expiring in 20): the expiry overlay would
        // read ExpiringSoon, but a not-yet-in-force cert can't assert "about to lapse" — it reads Pending.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(20), Today.AddDays(5), Today)
            .Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public void Future_effective_does_not_mask_a_NonCompliant_verdict()
    {
        // A future-effective cert that FAILS its rules is accurately "not compliant" — the demotion only
        // ever moves a doc OUT of the affirmative tally, never masks a hard fail.
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, Today.AddDays(300), Today.AddDays(30), Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void Expired_wins_over_a_future_effective_date()
    {
        // A malformed cert (EffectiveDate after today AND ExpirationDate before today, i.e. eff > exp):
        // Expired is top precedence and never demotes to Pending.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(-1), Today.AddDays(30), Today)
            .Should().Be(ComplianceStatus.Expired);
    }

    [Fact]
    public void Effective_today_is_in_force_and_is_not_demoted()
    {
        // Strict `>` on the effective boundary: a cert effective EXACTLY today is in force now, so an
        // affirmative verdict is NOT demoted. (One day earlier still in force; one day later demotes.)
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(300), Today, Today)
            .Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public void The_day_the_policy_becomes_effective_the_verdict_self_heals_from_Pending()
    {
        // AC (f): the SAME stored Compliant verdict reads Pending the day before it takes effect and
        // Compliant the day it does — the demotion is a pure read overlay driven by `today`, so the doc
        // self-heals with no re-evaluation the instant the calendar reaches its EffectiveDate.
        var effectiveDate = Today.AddDays(1);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(300), effectiveDate, Today)
            .Should().Be(ComplianceStatus.Pending, "the day before it takes effect it is not yet in force");
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(300), effectiveDate, effectiveDate)
            .Should().Be(ComplianceStatus.Compliant, "the day it takes effect the real verdict surfaces");
    }

    [Fact]
    public void Future_effective_time_component_is_ignored_on_the_boundary()
    {
        // A time-bearing effective date on tomorrow at any wall-clock time is still future-effective
        // (date comparison), matching the SQL instant bound (EffectiveDate >= today+1 midnight).
        var tomorrowNoon = Today.AddDays(1).AddHours(12);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(300), tomorrowNoon, Today)
            .Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public void NotYetEffectiveLowerBoundInclusive_is_the_instant_equivalent_of_the_future_effective_test()
    {
        // The SQL read sites compare a raw timestamptz EffectiveDate against this bound to reproduce the
        // deriver's date test (eff.Date > today). It must be UTC midnight of today+1, time-independent.
        var todayNoon = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var bound = ComplianceStatusDeriver.NotYetEffectiveLowerBoundInclusive(todayNoon);
        bound.Should().Be(new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc));

        // Effective today (midnight or noon) is in force — below the bound; effective tomorrow is not.
        (new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc) < bound).Should().BeTrue("effective today is in force");
        (new DateTime(2026, 6, 15, 23, 0, 0, DateTimeKind.Utc) < bound).Should().BeTrue("effective today at any time is in force");
        (new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc) >= bound).Should().BeTrue("effective tomorrow is not yet in force");
    }
}
