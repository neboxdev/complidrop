using System.Linq.Expressions;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// The single source of truth for the DATE-driven part of a document's compliance verdict (#257).
/// <see cref="ComplianceStatus"/> is a write-once cache computed at evaluation time, so as the
/// calendar advances it goes stale: a Compliant COI silently stays green past its expiration date.
/// This helper overlays the date verdict onto the stored rule verdict so every read site (document
/// list/detail, dashboard, export) and the nightly sweep agree on what a doc's status is *today*,
/// rather than what it was when last evaluated.
///
/// The overlay mirrors <see cref="ComplianceCheckService"/>'s own date precedence exactly, so a
/// freshly-evaluated doc and a swept/derived doc never disagree:
///   * past its expiration date            -> Expired (top precedence, overrides any rule verdict);
///   * within 30 days of expiring AND the rule verdict isn't a hard fail -> ExpiringSoon;
///   * NOT YET in force (EffectiveDate is a date strictly after today) AND the verdict would otherwise
///     read Compliant or ExpiringSoon -> Pending ("not yet in force"), #362 / ADR 0041;
///   * NEVER GRADED (no ComplianceCheck row — nothing was ever measured against it) AND the verdict
///     would otherwise read Compliant or ExpiringSoon -> Pending, #443 / ADR 0048;
///   * otherwise                           -> the stored rule verdict unchanged.
/// A NonCompliant verdict (rules failed) is preserved when merely expiring-soon OR future-effective — a
/// failing doc is still failing, and a not-yet-active deficient cert is accurately not-compliant — but an
/// EXPIRED doc reads Expired regardless, matching the service.
/// <para/>
/// The future-effective demotion is a READ-ONLY overlay: the persisted <see cref="ComplianceStatus"/>
/// keeps the real rule verdict (Compliant / ExpiringSoon), and only the effective status a reader sees is
/// Pending while the policy is not yet in force. This is deliberate and load-bearing — it is what lets the
/// doc SELF-HEAL to its real verdict the instant the calendar reaches its EffectiveDate, exactly as the
/// Expired / ExpiringSoon overlay does, with no re-evaluation. Storing Pending would strand the doc at a
/// stale Pending after it became effective (nothing re-runs rule evaluation on an EffectiveDate crossing),
/// so <see cref="ComplianceCheckService.ComputeOutcome"/> and the nightly sweep deliberately do NOT persist
/// this demotion; every read surface applies it instead. See ADR 0041.
/// <para/>
/// The never-graded demotion (#443 / ADR 0048) is read-only for the SAME reason, and the shape is
/// deliberately identical: an affirmative verdict the engine never actually measured anything to reach
/// reads Pending. It self-heals the moment the document IS graded (a governing rule is added, its type is
/// corrected, a checklist is assigned — each of which re-evaluates and writes check rows), and persisting
/// Pending would instead strand it, because writing Pending is also how the extraction worker claims a
/// document. <see cref="DocumentGrading"/> answers "was it graded"; this deriver owns what that ANSWER
/// means for the status a reader sees.
/// </summary>
public static class ComplianceStatusDeriver
{
    /// <summary>Days-before-expiry window that reads as <see cref="ComplianceStatus.ExpiringSoon"/>.</summary>
    public const int ExpiringSoonWindowDays = 30;

    /// <summary>
    /// The EXCLUSIVE upper instant a SQL read site (documents-list filter, dashboard counts, the
    /// nightly sweep) must compare a raw <c>timestamptz</c> <see cref="Document.ExpirationDate"/>
    /// against to reproduce the SAME "expires within <paramref name="withinDays"/> calendar days"
    /// window this deriver applies on read.
    ///
    /// The deriver buckets by calendar DATE (<c>exp.Date &lt;= today + N</c>). The instant-equivalent
    /// is <c>exp &lt; (today.Date + (N + 1) days)</c> — so a time-bearing expiry that lands on day N
    /// (e.g. noon UTC) is still inside the window, exactly as the derived badge shows it. Comparing a
    /// raw timestamptz against <c>today + N</c> at midnight instead drops such a doc out of the window
    /// in SQL while the badge keeps it in: the two-answers bug #294 fixes. The lower bounds
    /// (<c>exp &lt; today</c> for Expired, <c>exp &gt;= today</c> for "not yet expired") are already
    /// date-equivalent at UTC midnight and need no shift — only the inclusive upper edge does.
    ///
    /// The returned bound is a UTC-midnight instant (<paramref name="today"/> is a UTC date), so the
    /// comparison stays a plain instant-vs-instant test: no <c>::date</c> / <c>date_trunc</c>
    /// truncation, no <c>AT TIME ZONE</c>, no session-TimeZone dependence (ADR 0009). See
    /// <see href="../../../docs/adr/0027-compliance-date-window-boundaries.md">ADR 0027</see>.
    /// </summary>
    public static DateTime WindowUpperBoundExclusive(DateTime today, int withinDays) =>
        today.Date.AddDays(withinDays + 1);

    /// <summary>
    /// The INCLUSIVE lower instant a SQL read site compares a raw <c>timestamptz</c>
    /// <see cref="Document.EffectiveDate"/> against to reproduce the deriver's "not yet in force today"
    /// test (<c>effectiveDate.Date &gt; today</c>). The instant-equivalent is
    /// <c>effectiveDate &gt;= today + 1 day</c> at UTC midnight: an EffectiveDate on <paramref name="today"/>
    /// (or earlier) is in force, one on any later calendar day — at any wall-clock time — is not. Same
    /// date↔instant convention as <see cref="WindowUpperBoundExclusive"/> (ADR 0027): EffectiveDate is a
    /// face date stored at UTC midnight (<c>CanonicalDocumentFields.ParseUtcDate</c>), so the comparison
    /// stays a plain instant-vs-instant test — no <c>::date</c>, no <c>AT TIME ZONE</c>, no session-TZ
    /// dependence (ADR 0009). #362 / ADR 0041.
    /// </summary>
    public static DateTime NotYetEffectiveLowerBoundInclusive(DateTime today) => today.Date.AddDays(1);

    /// <summary>
    /// True when the document is NOT YET in force as of <paramref name="today"/> — its
    /// <paramref name="effectiveDate"/> is a calendar date strictly after today (#362 / ADR 0041). A
    /// future-effective certificate provides no coverage in force today, so it cannot assert a present-tense
    /// affirmative verdict. A null effective date (unknown / not extracted) is treated as in force — the
    /// same non-blocking default the rest of the pipeline uses for a missing date.
    /// </summary>
    public static bool IsFutureEffective(DateTime? effectiveDate, DateTime today) =>
        effectiveDate is DateTime eff && eff.Date > today.Date;

    /// <summary>
    /// The SQL mirror of "this document READS <see cref="ComplianceStatus.Pending"/> today" — the whole
    /// effective-Pending population: a genuinely-stored Pending outside the expiry window, plus every
    /// affirmative verdict the future-effective (#362 / ADR 0041) or never-graded (#443 / ADR 0048)
    /// demotion moves here. Expired wins outright, so every arm carries the not-yet-expired guard.
    /// <para/>
    /// A whole-entity <c>Expression</c> — the one shape an EF read site CAN compose (the
    /// <see cref="DocumentSupersession"/> pattern) — precisely because it has TWO consumers that must
    /// never disagree: the documents-list <c>?status=Pending</c> arm and the dashboard's
    /// <c>awaitingReview</c> count that deep-links to it. Spelling that predicate twice is the #294
    /// count-vs-list split this ADR exists to prevent, so it is spelled once.
    /// <para/>
    /// The sibling Compliant / ExpiringSoon arms in <c>DocumentEndpoints</c> are the same SHAPE — each
    /// is its own top-level <c>query.Where(d =&gt; …)</c>, and each could equally take an
    /// <c>Expression</c>. They stay inline because each has exactly ONE consumer, so there is no
    /// two-consumer drift to spend a shared predicate on; extracting them would add public surface
    /// nothing else calls. (Do not read this as "they structurally cannot be shared" — they can.) The
    /// sites that genuinely cannot invoke an <c>Expression</c> are the dashboard's counts, where the
    /// grading/effective-date facts are single clauses of a multi-clause lambda — most sharply the
    /// compliance-rate denominator, which needs them NEGATED inside a larger composite — and the
    /// list/rollup/export projections, which need the check COUNT as a projected scalar rather than a
    /// predicate at all. Those spell the fact inline as <c>d.ComplianceChecks.Any()</c> /
    /// <c>.Count</c>, and are covered by the count-vs-deep-linked-list pins instead.
    /// <para/>
    /// The demotion arms are deliberately INDEPENDENT of one another: a document can be both
    /// future-effective and never-graded, and either alone must land it here so the list matches the
    /// badge <see cref="Effective"/> renders.
    /// </summary>
    public static Expression<Func<Document, bool>> ReadsPending(DateTime today)
    {
        var expiringSoonUpperExclusive = WindowUpperBoundExclusive(today, ExpiringSoonWindowDays);
        var notYetEffectiveBound = NotYetEffectiveLowerBoundInclusive(today);
        var todayDate = today.Date;
        return d =>
            // Genuine Pending — stored Pending, not date-overlaid to ExpiringSoon/Expired.
            (d.ComplianceStatus == ComplianceStatus.Pending
                && (d.ExpirationDate == null || d.ExpirationDate >= expiringSoonUpperExclusive))
            // OR the future-effective demotion (#362): a not-yet-in-force cert whose stored verdict is
            // affirmative (Compliant / ExpiringSoon / Pending) and which isn't already Expired.
            || (d.EffectiveDate != null && d.EffectiveDate >= notYetEffectiveBound
                && (d.ExpirationDate == null || d.ExpirationDate >= todayDate)
                && (d.ComplianceStatus == ComplianceStatus.Compliant
                    || d.ComplianceStatus == ComplianceStatus.ExpiringSoon
                    || d.ComplianceStatus == ComplianceStatus.Pending))
            // OR the never-graded demotion (#443 / ADR 0048): the same clause on the grading axis — an
            // affirmative stored verdict (or one the expiry window would promote) that no ComplianceCheck
            // row backs. `d.ComplianceChecks.Any()` is the SQL spelling of DocumentGrading.IsGraded.
            || (!d.ComplianceChecks.Any()
                && (d.ExpirationDate == null || d.ExpirationDate >= todayDate)
                && (d.ComplianceStatus == ComplianceStatus.Compliant
                    || d.ComplianceStatus == ComplianceStatus.ExpiringSoon
                    || d.ComplianceStatus == ComplianceStatus.Pending));
    }

    /// <summary>
    /// Derives the effective status shown to the user as of <paramref name="today"/> (a date; the
    /// time component is ignored). <paramref name="today"/> is passed in — not read from the clock —
    /// so callers stay deterministically testable and consistent with the rest of the codebase's
    /// UTC-date convention. <paramref name="effectiveDate"/> demotes an affirmative verdict to Pending
    /// while the policy is not yet in force (#362 / ADR 0041); <paramref name="isGraded"/> demotes one
    /// no requirement was ever measured to produce (#443 / ADR 0048).
    /// <para/>
    /// <paramref name="isGraded"/> is REQUIRED rather than defaulted on purpose: a default would be
    /// fail-open (a new read surface that forgot it would silently re-assert the coverage #443 removed),
    /// and it is the same forcing function ADR 0041 used when it added <paramref name="effectiveDate"/>.
    /// Ask <see cref="DocumentGrading.IsGraded(int)"/> for the value — never re-derive the threshold.
    /// </summary>
    public static ComplianceStatus Effective(
        ComplianceStatus stored, DateTime? expirationDate, DateTime? effectiveDate, bool isGraded,
        DateTime today)
    {
        var todayDate = today.Date;

        ComplianceStatus overlaid;
        if (expirationDate is not DateTime exp)
            overlaid = stored; // no expiry date → nothing to overlay
        else
        {
            var expiry = exp.Date;
            // Expired is top precedence and is NEVER demoted — an expired cert is a present liability
            // regardless of any (necessarily malformed, EffectiveDate > ExpirationDate) effective date.
            if (expiry < todayDate) return ComplianceStatus.Expired;

            overlaid = expiry <= todayDate.AddDays(ExpiringSoonWindowDays)
                && stored is ComplianceStatus.Compliant or ComplianceStatus.ExpiringSoon or ComplianceStatus.Pending
                ? ComplianceStatus.ExpiringSoon
                : stored;
        }

        // Future-effective demotion (#362 / ADR 0041): a cert not yet in force can't assert present-tense
        // coverage, so an affirmative overlaid verdict (Compliant or ExpiringSoon) reads Pending instead.
        // NonCompliant (a not-yet-active deficient cert is accurately not-compliant) and Expired (returned
        // above) are never demoted — the demotion only ever moves a doc OUT of the compliant tally.
        if (overlaid is ComplianceStatus.Compliant or ComplianceStatus.ExpiringSoon
            && IsFutureEffective(effectiveDate, today))
            return ComplianceStatus.Pending;

        // Never-graded demotion (#443 / ADR 0048), the same clause on a third axis: the product may not
        // assert an affirmative verdict it never measured anything to reach. Placed AFTER the expiry
        // if/else (like the future-effective clause, and for the same reason — #362 review S2) so it also
        // catches the null-expiry path, and gated on the OVERLAID status so it demotes both a stored
        // ExpiringSoon and the stored Pending this deriver just promoted into one. Expired is returned
        // above and is never demoted (a lapsed date is a real, un-graded fact and a present liability);
        // NonCompliant is unreachable without a failed check row, and is excluded anyway so the demotion
        // can only ever move a doc OUT of the affirmative tally, never mask a hard fail.
        if (overlaid is ComplianceStatus.Compliant or ComplianceStatus.ExpiringSoon && !isGraded)
            return ComplianceStatus.Pending;

        return overlaid;
    }
}
