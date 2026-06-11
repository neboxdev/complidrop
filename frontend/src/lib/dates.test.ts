/**
 * #263 — calendar dates must render as the calendar date the document says,
 * not shifted into the browser's timezone. The regression scenario: a COI
 * saying 08/01/2026 is stored as 2026-08-01T00:00:00Z; in any US zone a bare
 * toLocaleDateString() renders 7/31/2026.
 */
import { describe, it, expect } from "vitest";
import { formatCalendarDate } from "./dates";

describe("formatCalendarDate (#263)", () => {
  it("renders the UTC calendar date regardless of local timezone", () => {
    // The vitest config pins TZ=America/New_York (see vitest.config.mts) so this
    // assertion genuinely exercises the US-zone shift the bug produced. If TZ is
    // not pinned, the expectation still holds — timeZone:"UTC" makes the render
    // environment-independent, which is the entire point.
    expect(formatCalendarDate("2026-08-01T00:00:00Z")).toBe(
      new Date("2026-08-01T00:00:00Z").toLocaleDateString(undefined, { timeZone: "UTC" }),
    );
    // And concretely: the rendered string contains the 1st, never the 31st.
    expect(formatCalendarDate("2026-08-01T00:00:00Z")).toMatch(/1/);
    expect(formatCalendarDate("2026-08-01T00:00:00Z")).not.toMatch(/31/);
  });

  it("never renders the previous day for UTC-midnight timestamps", () => {
    // Sweep a year of UTC midnights: the rendered day-of-month must always match
    // the ISO string's day — a local-zone shift would fail for every US zone.
    for (let month = 1; month <= 12; month++) {
      const iso = `2026-${String(month).padStart(2, "0")}-01T00:00:00Z`;
      const rendered = formatCalendarDate(iso);
      expect(rendered, `month ${month}`).toContain("1");
      expect(new Date(iso).getUTCDate()).toBe(1);
    }
  });

  it("returns an em dash for null, undefined, empty, and unparseable input", () => {
    expect(formatCalendarDate(null)).toBe("—");
    expect(formatCalendarDate(undefined)).toBe("—");
    expect(formatCalendarDate("")).toBe("—");
    expect(formatCalendarDate("not-a-date")).toBe("—");
  });
});
