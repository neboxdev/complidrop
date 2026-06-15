using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for the date-overlay that keeps compliance verdicts from going stale (#257).
/// The deriver must mirror ComplianceCheckService's date precedence exactly so a freshly-evaluated
/// doc and a swept/derived doc never disagree. No DB, no clock — today is passed in.
/// </summary>
public sealed class ComplianceStatusDeriverTests
{
    private static readonly DateTime Today = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Null_expiration_returns_stored_unchanged()
    {
        foreach (var stored in Enum.GetValues<ComplianceStatus>())
            ComplianceStatusDeriver.Effective(stored, null, Today).Should().Be(stored);
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
        ComplianceStatusDeriver.Effective(stored, Today.AddDays(-1), Today)
            .Should().Be(ComplianceStatus.Expired);
    }

    [Fact]
    public void Expiring_today_is_not_yet_expired()
    {
        // Strict `<` for Expired (mirrors the service): expiring exactly today is ExpiringSoon.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today, Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Theory]
    [InlineData(ComplianceStatus.Compliant)]
    [InlineData(ComplianceStatus.ExpiringSoon)]
    [InlineData(ComplianceStatus.Pending)]
    public void Within_window_overlays_ExpiringSoon_for_non_failing_verdicts(ComplianceStatus stored)
    {
        ComplianceStatusDeriver.Effective(stored, Today.AddDays(10), Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public void Within_window_keeps_a_hard_fail_NonCompliant()
    {
        // A failing doc is still failing even when expiring soon — the date doesn't soften the verdict.
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, Today.AddDays(10), Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void Exactly_30_days_is_within_the_window_but_31_is_not()
    {
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(30), Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(31), Today)
            .Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public void Far_future_expiration_returns_stored_unchanged()
    {
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(200), Today)
            .Should().Be(ComplianceStatus.Compliant);
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, Today.AddDays(200), Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void Time_component_of_today_is_ignored()
    {
        // A doc expiring today at any wall-clock time is still "today" — the overlay compares dates.
        var todayAfternoon = new DateTime(2026, 6, 15, 18, 30, 0, DateTimeKind.Utc);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today, todayAfternoon)
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
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, onBoundaryNoon, todayNoon)
            .Should().Be(ComplianceStatus.ExpiringSoon);

        // The day after the window (day 31 at noon) is OUTSIDE (`>= bound`), matching stored Compliant.
        var pastBoundaryNoon = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        (pastBoundaryNoon < bound).Should().BeFalse();
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, pastBoundaryNoon, todayNoon)
            .Should().Be(ComplianceStatus.Compliant);
    }
}
