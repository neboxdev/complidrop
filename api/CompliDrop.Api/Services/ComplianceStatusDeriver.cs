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
