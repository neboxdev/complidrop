import { describe, it, expect } from "vitest";
import { listTimeZones, describeNextSend } from "./timezones";

describe("listTimeZones (#185)", () => {
  it("returns a non-trivial IANA list that includes common zones", () => {
    const zones = listTimeZones();
    expect(zones.length).toBeGreaterThan(5);
    expect(zones).toContain("America/New_York");
  });

  it("prepends a current zone that's absent from the runtime list so it stays selectable", () => {
    // A stored zone the runtime doesn't enumerate (older engine, custom value)
    // must still appear so the <select> can render it as the selected option.
    const zones = listTimeZones("Custom/Unlisted");
    expect(zones[0]).toBe("Custom/Unlisted");
  });

  it("keeps a real, already-listed current zone present without duplicating it", () => {
    // Use a zone the runtime is guaranteed to enumerate (the first test pins
    // that America/New_York is in the raw list), so this genuinely exercises
    // the already-present branch rather than the prepend-when-absent fallback.
    expect(listTimeZones()).toContain("America/New_York");
    const zones = listTimeZones("America/New_York");
    expect(zones).toContain("America/New_York");
    expect(zones.filter((z) => z === "America/New_York")).toHaveLength(1);
  });

  it("does not duplicate the current zone when it's already in the list", () => {
    const zones = listTimeZones("America/New_York");
    expect(zones.filter((z) => z === "America/New_York")).toHaveLength(1);
  });
});

describe("describeNextSend (#185)", () => {
  // 2026-01-15T12:00:00Z = 07:00 in America/New_York (EST, UTC-5) → before 8am → today.
  const noonUtc = new Date("2026-01-15T12:00:00Z");
  // 2026-01-15T18:00:00Z = 13:00 in America/New_York → after 8am → tomorrow.
  const eveningUtc = new Date("2026-01-15T18:00:00Z");

  it("says 'today' before 8am local", () => {
    expect(describeNextSend("America/New_York", noonUtc)).toMatch(/next one goes out today/);
  });

  it("says 'tomorrow' at/after 8am local", () => {
    expect(describeNextSend("America/New_York", eveningUtc)).toMatch(/next one goes out tomorrow/);
  });

  it("always states the 8:00 AM send time so the zone's effect is visible", () => {
    expect(describeNextSend("America/Chicago", noonUtc)).toMatch(/8:00 AM/);
  });

  it("reflects the zone: same instant is 'today' in NY but 'tomorrow' further east", () => {
    // 12:00Z is 07:00 NY (before 8 → today) but 21:00 Tokyo (after 8 → tomorrow).
    expect(describeNextSend("America/New_York", noonUtc)).toMatch(/today/);
    expect(describeNextSend("Asia/Tokyo", noonUtc)).toMatch(/tomorrow/);
  });
});
