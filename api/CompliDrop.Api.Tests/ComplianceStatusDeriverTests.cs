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
            ComplianceStatusDeriver.Effective(stored, null, null, isGraded: true, Today).Should().Be(stored);
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
        ComplianceStatusDeriver.Effective(stored, Today.AddDays(-1), null, isGraded: true, Today)
            .Should().Be(ComplianceStatus.Expired);
    }

    [Fact]
    public void Expiring_today_is_not_yet_expired()
    {
        // Strict `<` for Expired (mirrors the service): expiring exactly today is ExpiringSoon.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today, null, isGraded: true, Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Theory]
    [InlineData(ComplianceStatus.Compliant)]
    [InlineData(ComplianceStatus.ExpiringSoon)]
    [InlineData(ComplianceStatus.Pending)]
    public void Within_window_overlays_ExpiringSoon_for_non_failing_verdicts(ComplianceStatus stored)
    {
        ComplianceStatusDeriver.Effective(stored, Today.AddDays(10), null, isGraded: true, Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public void Within_window_keeps_a_hard_fail_NonCompliant()
    {
        // A failing doc is still failing even when expiring soon — the date doesn't soften the verdict.
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, Today.AddDays(10), null, isGraded: true, Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void Exactly_30_days_is_within_the_window_but_31_is_not()
    {
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(30), null, isGraded: true, Today)
            .Should().Be(ComplianceStatus.ExpiringSoon);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(31), null, isGraded: true, Today)
            .Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public void Far_future_expiration_returns_stored_unchanged()
    {
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(200), null, isGraded: true, Today)
            .Should().Be(ComplianceStatus.Compliant);
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, Today.AddDays(200), null, isGraded: true, Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void Time_component_of_today_is_ignored()
    {
        // A doc expiring today at any wall-clock time is still "today" — the overlay compares dates.
        var todayAfternoon = new DateTime(2026, 6, 15, 18, 30, 0, DateTimeKind.Utc);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today, null, isGraded: true, todayAfternoon)
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
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, onBoundaryNoon, null, isGraded: true, todayNoon)
            .Should().Be(ComplianceStatus.ExpiringSoon);

        // The day after the window (day 31 at noon) is OUTSIDE (`>= bound`), matching stored Compliant.
        var pastBoundaryNoon = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        (pastBoundaryNoon < bound).Should().BeFalse();
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, pastBoundaryNoon, null, isGraded: true, todayNoon)
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
        ComplianceStatusDeriver.Effective(stored, Today.AddDays(300), Today.AddDays(30), isGraded: true, Today)
            .Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public void Future_effective_within_the_expiring_window_still_reads_Pending_not_ExpiringSoon()
    {
        // A short future-effective policy (effective in 5 days, expiring in 20): the expiry overlay would
        // read ExpiringSoon, but a not-yet-in-force cert can't assert "about to lapse" — it reads Pending.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(20), Today.AddDays(5), isGraded: true, Today)
            .Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public void Future_effective_does_not_mask_a_NonCompliant_verdict()
    {
        // A future-effective cert that FAILS its rules is accurately "not compliant" — the demotion only
        // ever moves a doc OUT of the affirmative tally, never masks a hard fail.
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, Today.AddDays(300), Today.AddDays(30), isGraded: true, Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void Expired_wins_over_a_future_effective_date()
    {
        // A malformed cert (EffectiveDate after today AND ExpirationDate before today, i.e. eff > exp):
        // Expired is top precedence and never demotes to Pending.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(-1), Today.AddDays(30), isGraded: true, Today)
            .Should().Be(ComplianceStatus.Expired);
    }

    [Fact]
    public void Effective_today_is_in_force_and_is_not_demoted()
    {
        // Strict `>` on the effective boundary: a cert effective EXACTLY today is in force now, so an
        // affirmative verdict is NOT demoted. (One day earlier still in force; one day later demotes.)
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(300), Today, isGraded: true, Today)
            .Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public void The_day_the_policy_becomes_effective_the_verdict_self_heals_from_Pending()
    {
        // AC (f): the SAME stored Compliant verdict reads Pending the day before it takes effect and
        // Compliant the day it does — the demotion is a pure read overlay driven by `today`, so the doc
        // self-heals with no re-evaluation the instant the calendar reaches its EffectiveDate.
        var effectiveDate = Today.AddDays(1);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(300), effectiveDate, isGraded: true, Today)
            .Should().Be(ComplianceStatus.Pending, "the day before it takes effect it is not yet in force");
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(300), effectiveDate, isGraded: true, effectiveDate)
            .Should().Be(ComplianceStatus.Compliant, "the day it takes effect the real verdict surfaces");
    }

    [Fact]
    public void Future_effective_time_component_is_ignored_on_the_boundary()
    {
        // A time-bearing effective date on tomorrow at any wall-clock time is still future-effective
        // (date comparison), matching the SQL instant bound (EffectiveDate >= today+1 midnight).
        var tomorrowNoon = Today.AddDays(1).AddHours(12);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(300), tomorrowNoon, isGraded: true, Today)
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

    [Fact]
    public void Null_expiry_with_a_future_effective_date_demotes_an_affirmative_verdict_to_Pending()
    {
        // #362 review S2: a doc with NO expiration but a FUTURE effective date takes the null-expiry
        // (overlaid = stored) branch and THEN the future-effective demotion — so a stored Compliant reads
        // Pending. This guards the demotion's placement AFTER the expiry if/else: moving it inside the
        // non-null-expiry (else) block would leave this reading Compliant while the dashboard compliant
        // count and the ?status=Pending list arm (both of which carry an ExpirationDate == null branch)
        // demote it — a silent count-vs-badge split. Every other #362 test uses a non-null expiry, so
        // without this the null-expiry path of the demotion is untested.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, expirationDate: null, effectiveDate: Today.AddDays(30), isGraded: true, Today)
            .Should().Be(ComplianceStatus.Pending);
        // Control: a null-expiry future-effective doc that FAILS its rules is accurately NonCompliant,
        // never demoted — the demotion only ever moves a doc OUT of the affirmative tally.
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, expirationDate: null, effectiveDate: Today.AddDays(30), isGraded: true, Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    // ---- #443 / ADR 0048: never-graded (zero applicable rules) demotion ----

    [Fact]
    public void Never_graded_inside_the_expiry_window_reads_Pending_not_ExpiringSoon()
    {
        // THE bug. ComputeOutcome's zero-applicable-rules branch stores `expiringSoon ? ExpiringSoon :
        // Pending`, so a never-graded doc 10 days from expiry was stored ExpiringSoon — and every surface
        // that groups ExpiringSoon with Compliant then asserted coverage nothing had established.
        ComplianceStatusDeriver.Effective(ComplianceStatus.ExpiringSoon, Today.AddDays(10), null, isGraded: false, Today)
            .Should().Be(ComplianceStatus.Pending);
        // …and the other half of the chain: the overlay used to PROMOTE a stored Pending into the same
        // affirmative ExpiringSoon. Both stored values must land on Pending, or the two halves disagree.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Pending, Today.AddDays(10), null, isGraded: false, Today)
            .Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public void The_grading_axis_no_longer_turns_on_the_expiry_date()
    {
        // The tell that the pre-#443 behaviour was arbitrary: the SAME absence of grading read Pending at
        // 31 days to expiry and ExpiringSoon at 29. The date decided whether the product claimed coverage.
        // Both sides of the window boundary must now read Pending.
        foreach (var days in new[] { 0, 1, 29, 30, 31, 200 })
            ComplianceStatusDeriver.Effective(ComplianceStatus.ExpiringSoon, Today.AddDays(days), null, isGraded: false, Today)
                .Should().Be(ComplianceStatus.Pending, $"a never-graded doc expiring in {days} days certifies nothing");
    }

    [Fact]
    public void Never_graded_demotes_a_stored_Compliant_too()
    {
        // Not reachable from ComputeOutcome (an affirmative verdict always comes with its check rows), but
        // reachable transiently: deleting a rule hard-deletes its check rows and re-grades AFTER the
        // commit, and the seed's orphan cleanup does the same. If that re-grade never lands, the stored
        // Compliant is backed by nothing — fail CLOSED rather than certify off missing evidence.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(200), null, isGraded: false, Today)
            .Should().Be(ComplianceStatus.Pending);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, expirationDate: null, effectiveDate: null, isGraded: false, Today)
            .Should().Be(ComplianceStatus.Pending, "the demotion sits after the expiry if/else, so it catches the null-expiry path too");
    }

    [Fact]
    public void Never_graded_does_not_soften_Expired_or_NonCompliant()
    {
        // Expired is top precedence and is never demoted: a lapsed date is a real, un-graded fact and a
        // present liability. The demotion only ever moves a doc OUT of the affirmative tally.
        ComplianceStatusDeriver.Effective(ComplianceStatus.ExpiringSoon, Today.AddDays(-1), null, isGraded: false, Today)
            .Should().Be(ComplianceStatus.Expired);
        ComplianceStatusDeriver.Effective(ComplianceStatus.Pending, Today.AddDays(-1), null, isGraded: false, Today)
            .Should().Be(ComplianceStatus.Expired);
        // NonCompliant is unreachable without a failed check row, but is excluded anyway so the clause can
        // never mask a hard fail (the ADR 0041 rule, on the grading axis).
        ComplianceStatusDeriver.Effective(ComplianceStatus.NonCompliant, Today.AddDays(10), null, isGraded: false, Today)
            .Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void A_graded_document_is_completely_unaffected_by_the_new_clause()
    {
        // The blast radius: with isGraded true, every stored verdict × date combination must derive exactly
        // what it did before #443. This is the regression fence around the whole change.
        foreach (var stored in Enum.GetValues<ComplianceStatus>())
            foreach (var days in new int?[] { null, -1, 0, 10, 30, 31, 200 })
            {
                var exp = days is int d ? Today.AddDays(d) : (DateTime?)null;
                var expected = exp is null ? stored
                    : days < 0 ? ComplianceStatus.Expired
                    : days <= 30 && stored is ComplianceStatus.Compliant or ComplianceStatus.ExpiringSoon or ComplianceStatus.Pending
                        ? ComplianceStatus.ExpiringSoon
                        : stored;
                ComplianceStatusDeriver.Effective(stored, exp, null, isGraded: true, Today)
                    .Should().Be(expected, $"stored {stored} expiring in {days?.ToString() ?? "never"} days is unchanged when graded");
            }
    }

    [Fact]
    public void The_verdict_self_heals_the_moment_the_document_is_graded()
    {
        // The read-only-overlay guarantee, the grading twin of the future-effective self-heal above: the
        // SAME stored verdict reads Pending while nothing has graded the doc and its real value the instant
        // a check row exists. Nothing is persisted, so adding a governing rule (or correcting the doc's
        // type) recovers the verdict with no re-write of ComplianceStatus.
        ComplianceStatusDeriver.Effective(ComplianceStatus.ExpiringSoon, Today.AddDays(10), null, isGraded: false, Today)
            .Should().Be(ComplianceStatus.Pending, "nothing has been measured against it yet");
        ComplianceStatusDeriver.Effective(ComplianceStatus.ExpiringSoon, Today.AddDays(10), null, isGraded: true, Today)
            .Should().Be(ComplianceStatus.ExpiringSoon, "the stored verdict surfaces the moment a requirement is checked");
    }

    [Fact]
    public void Never_graded_and_future_effective_are_independent_clauses()
    {
        // A doc can be both. Either alone demotes, and both together still land on Pending — so no read
        // site can satisfy one clause and skip the other and still look correct.
        ComplianceStatusDeriver.Effective(ComplianceStatus.Compliant, Today.AddDays(300), Today.AddDays(30), isGraded: false, Today)
            .Should().Be(ComplianceStatus.Pending);
    }
}
