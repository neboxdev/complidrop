namespace CompliDrop.Api.RuleEngine;

/// <summary>Where the evaluation date sits relative to a computed next-due date (with its grace window).</summary>
public enum CadenceTiming
{
    /// <summary>Before the expiring-soon window opens.</summary>
    NotYetDue,

    /// <summary>Inside the expiring-soon window, up to and including the end of grace.</summary>
    DueSoon,

    /// <summary>Past the due date + grace period.</summary>
    Overdue,

    /// <summary>No recurring deadline (one-time obligation, or no computable anchor).</summary>
    NoDeadline
}

/// <summary>
/// Pure, DATE-ONLY cadence arithmetic (SCHEMA §5). Given a cadence block, the tracked document's dates,
/// and the evaluation date, it computes the next-due date and classifies the evaluation date against it.
/// Everything is <see cref="DateOnly"/>: there is no time-of-day and no time zone, so DST cannot shift a
/// result — the same reason the arithmetic is deterministic and reproducible.
///
/// Month arithmetic clamps to the end of the target month and honours leap years via
/// <see cref="DateOnly.AddMonths"/> (Jan 31 + 1mo ⇒ Feb 28/29; Feb 29 + 12mo ⇒ Feb 28 in a common year).
/// </summary>
public static class CadenceCalculator
{
    /// <summary>
    /// Adds <paramref name="months"/> calendar months, clamping the day to the last valid day of the
    /// resulting month. Deterministic; leap-year aware. (Delegates to <see cref="DateOnly.AddMonths"/>,
    /// which already clamps — wrapped so the cadence's month math has one named, testable home and can
    /// never be replaced by a naive hand-rolled version without a failing test.)
    /// </summary>
    public static DateOnly AddMonthsClamped(DateOnly date, int months) => date.AddMonths(months);

    /// <summary>
    /// The next occurrence of the calendar <paramref name="month"/>/<paramref name="day"/> on or after
    /// <paramref name="onOrAfter"/>. A Feb-29 target clamps to Feb 28 in a common year. Used for
    /// fixed-annual filings (e.g. franchise tax May 15).
    /// </summary>
    public static DateOnly NextAnnualOccurrence(DateOnly onOrAfter, int month, int day)
    {
        var thisYear = ClampToMonth(onOrAfter.Year, month, day);
        return thisYear >= onOrAfter ? thisYear : ClampToMonth(onOrAfter.Year + 1, month, day);
    }

    private static DateOnly ClampToMonth(int year, int month, int day)
    {
        var daysInMonth = DateTime.DaysInMonth(year, month);
        return new DateOnly(year, month, Math.Min(day, daysInMonth));
    }

    /// <summary>
    /// Computes the next-due date for a cadence given the tracked document's expiry / issue date and the
    /// evaluation date. Returns null when no deadline is determinable (a one-time obligation, or a
    /// renewal/filing whose anchor data isn't present). SCHEMA §5: v1 leans on the document's own printed
    /// expiry where it carries one; the period-from-issue and fixed-date branches are the fallbacks.
    /// </summary>
    public static DateOnly? ComputeNextDueDate(
        Cadence cadence,
        DateOnly? documentExpiration,
        DateOnly? issueDate,
        DateOnly evaluationDate)
    {
        switch (cadence.Anchor)
        {
            case CadenceAnchor.DocumentExpiration:
                // The printed expiry IS the renewal deadline. Fall back to issue + period when the tracked
                // document carries no printed expiry (SCHEMA §5's "documents without printed expiry" case).
                if (documentExpiration is { } printed) return printed;
                if (issueDate is { } issuedA && cadence.PeriodMonths is { } monthsA)
                    return AddMonthsClamped(issuedA, monthsA);
                return null;

            case CadenceAnchor.IssueDate:
                if (issueDate is { } issuedB && cadence.PeriodMonths is { } monthsB)
                    return AddMonthsClamped(issuedB, monthsB);
                return null;

            case CadenceAnchor.CalendarDate:
            case CadenceAnchor.FixedDate:
                if (cadence.FixedDate is { } md)
                    return NextAnnualOccurrence(evaluationDate, md.Month, md.Day);
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Classifies <paramref name="evaluationDate"/> against a next-due date. Overdue once the date passes
    /// due + grace; DueSoon within <paramref name="expiringWindowDays"/> before due through the end of
    /// grace; otherwise NotYetDue. A null due date is <see cref="CadenceTiming.NoDeadline"/>.
    /// </summary>
    public static CadenceTiming ClassifyTiming(
        DateOnly? dueDate,
        DateOnly evaluationDate,
        int gracePeriodDays,
        int expiringWindowDays)
    {
        if (dueDate is not { } due) return CadenceTiming.NoDeadline;

        var graceEnd = due.AddDays(gracePeriodDays);
        if (evaluationDate > graceEnd) return CadenceTiming.Overdue;

        var windowOpens = due.AddDays(-expiringWindowDays);
        return evaluationDate >= windowOpens ? CadenceTiming.DueSoon : CadenceTiming.NotYetDue;
    }
}
