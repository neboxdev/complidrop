/**
 * Calendar-date rendering for date-only fields (#263).
 *
 * Document expiration/effective dates are date-only facts ("the COI says
 * 08/01/2026") that the API serializes as UTC-midnight timestamps
 * ("2026-08-01T00:00:00Z"). Rendering those through a bare
 * `toLocaleDateString()` shifts them into the browser's timezone — in any
 * US zone, UTC midnight is still the PREVIOUS evening, so every date
 * displayed one day early (and the bug is invisible in UTC+ zones, which is
 * exactly how it survives dev testing). Pinning `timeZone: "UTC"` renders
 * the calendar date the document actually says, everywhere.
 *
 * Use this for date-only semantics (expirationDate, effectiveDate). Do NOT
 * use it for real instants (sentAt, createdAt) — those are moments in time
 * and belong in the viewer's local zone via `toLocaleString()`.
 */
export function formatCalendarDate(iso: string | null | undefined): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleDateString(undefined, { timeZone: "UTC" });
}
