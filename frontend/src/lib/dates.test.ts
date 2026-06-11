/**
 * #263 — calendar dates must render as the calendar date the document says,
 * not shifted into the browser's timezone. The regression scenario: a COI
 * saying 08/01/2026 is stored as 2026-08-01T00:00:00Z; in any US zone a bare
 * toLocaleDateString() renders 7/31/2026.
 */
import { describe, it, expect } from "vitest";
import { formatCalendarDate } from "./dates";

describe("formatCalendarDate (#263)", () => {
  it("runs under the pinned US timezone (every regression assertion below depends on it)", () => {
    // vitest.config.mts pins TZ=America/New_York. If that pin ever silently stops
    // working (config refactor, runner change), the bare-local and UTC renders
    // become identical in UTC+ environments and the regression tests degrade to
    // tautologies — the exact invisibility mode that let the original bug ship.
    expect(Intl.DateTimeFormat().resolvedOptions().timeZone).toBe("America/New_York");
  });

  it("renders the UTC calendar date regardless of local timezone", () => {
    expect(formatCalendarDate("2026-08-01T00:00:00Z")).toBe(
      new Date("2026-08-01T00:00:00Z").toLocaleDateString(undefined, { timeZone: "UTC" }),
    );
    // And concretely: never the local-shifted previous day (7/31).
    expect(formatCalendarDate("2026-08-01T00:00:00Z")).not.toMatch(/31/);
  });

  it("never renders the previous day for UTC-midnight timestamps", () => {
    // Sweep a year of UTC midnights. Every iteration is load-bearing: under the
    // pinned UTC-minus zone the bare local render is the PREVIOUS day for every
    // UTC midnight, so the not-equal assertion catches both a regression to
    // local-shifted rendering AND a silently disarmed TZ pin.
    for (let month = 1; month <= 12; month++) {
      const iso = `2026-${String(month).padStart(2, "0")}-01T00:00:00Z`;
      const rendered = formatCalendarDate(iso);
      expect(rendered, `month ${month}`).toBe(
        new Date(iso).toLocaleDateString(undefined, { timeZone: "UTC" }),
      );
      expect(rendered, `month ${month}`).not.toBe(new Date(iso).toLocaleDateString());
    }
  });

  it("returns an em dash for null, undefined, empty, and unparseable input", () => {
    expect(formatCalendarDate(null)).toBe("—");
    expect(formatCalendarDate(undefined)).toBe("—");
    expect(formatCalendarDate("")).toBe("—");
    expect(formatCalendarDate("not-a-date")).toBe("—");
  });
});
