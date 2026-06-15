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
///   * otherwise                           -> the stored rule verdict unchanged.
/// A NonCompliant verdict (rules failed) is preserved when merely expiring-soon — a failing doc is
/// still failing — but an EXPIRED doc reads Expired regardless, matching the service.
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
    /// Derives the effective status shown to the user as of <paramref name="today"/> (a date; the
    /// time component is ignored). <paramref name="today"/> is passed in — not read from the clock —
    /// so callers stay deterministically testable and consistent with the rest of the codebase's
    /// UTC-date convention.
    /// </summary>
    public static ComplianceStatus Effective(ComplianceStatus stored, DateTime? expirationDate, DateTime today)
    {
        if (expirationDate is not DateTime exp) return stored; // no date → nothing to overlay
        var expiry = exp.Date;
        var todayDate = today.Date;

        if (expiry < todayDate) return ComplianceStatus.Expired;

        if (expiry <= todayDate.AddDays(ExpiringSoonWindowDays)
            && stored is ComplianceStatus.Compliant or ComplianceStatus.ExpiringSoon or ComplianceStatus.Pending)
            return ComplianceStatus.ExpiringSoon;

        return stored;
    }
}
