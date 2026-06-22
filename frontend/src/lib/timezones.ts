// IANA time-zone helpers for the org settings form (#185). The org time zone
// silently drives when reminders fire (org-local 08:00), so Settings lets the
// user pick it and previews the effect.

// Curated fallback if the runtime lacks `Intl.supportedValuesOf` (older
// engines). Covers the US zones the SMB beachhead lives in plus UTC; the user's
// current zone is always prepended by `listTimeZones` so it's never missing.
const FALLBACK_ZONES = [
  "America/New_York",
  "America/Chicago",
  "America/Denver",
  "America/Phoenix",
  "America/Los_Angeles",
  "America/Anchorage",
  "Pacific/Honolulu",
  "UTC",
];

/**
 * Curated, friendly-labeled US-first zones for the top of the settings picker (#320 FP-112).
 * The raw ~400-entry IANA wheel (starting "Africa/Abidjan") made finding your own zone the hard
 * part; these cover the SMB beachhead. The full IANA list is still offered below as "All time zones".
 */
export const CURATED_TIMEZONES: ReadonlyArray<{ value: string; label: string }> = [
  { value: "America/New_York", label: "Eastern Time — New York" },
  { value: "America/Chicago", label: "Central Time — Chicago" },
  { value: "America/Denver", label: "Mountain Time — Denver" },
  { value: "America/Phoenix", label: "Mountain Time (no DST) — Phoenix" },
  { value: "America/Los_Angeles", label: "Pacific Time — Los Angeles" },
  { value: "America/Anchorage", label: "Alaska Time — Anchorage" },
  { value: "Pacific/Honolulu", label: "Hawaii Time — Honolulu" },
  { value: "UTC", label: "UTC" },
];

/**
 * The full IANA zone list for the `<select>`, with `current` guaranteed present
 * and first (so a saved-but-unusual zone always renders selected). Uses
 * `Intl.supportedValuesOf("timeZone")` — the canonical list — when available.
 */
export function listTimeZones(current?: string): string[] {
  let zones: string[] = [];
  try {
    const supported = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] })
      .supportedValuesOf;
    if (typeof supported === "function") zones = supported("timeZone");
  } catch {
    /* fall through to the curated list */
  }
  if (zones.length === 0) zones = [...FALLBACK_ZONES];
  if (current && !zones.includes(current)) return [current, ...zones];
  return zones;
}

/** The current wall-clock hour (0–23) in `timeZone`. */
function hourInZone(timeZone: string, now: Date): number {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone,
    hour: "numeric",
    hour12: false,
  }).formatToParts(now);
  const raw = Number(parts.find((p) => p.type === "hour")?.value ?? "0");
  // Some engines render midnight as "24"; normalize to 0 so the < 8 test holds.
  return raw % 24;
}

/**
 * Human sentence describing when the next daily reminder will fire for a given
 * zone, so the zone's effect is visible as the user changes the select.
 * Reminders fire at org-local 08:00 (ReminderBackgroundService), so the next
 * send is today if it's before 08:00 locally, otherwise tomorrow.
 *
 * `now` is injectable for deterministic tests.
 */
export function describeNextSend(timeZone: string, now: Date = new Date()): string {
  let localTime: string;
  try {
    localTime = new Intl.DateTimeFormat("en-US", {
      timeZone,
      hour: "numeric",
      minute: "2-digit",
      hour12: true,
    }).format(now);
  } catch {
    return "Reminders send at 8:00 AM in your organization's time zone.";
  }
  // "Next FUTURE send": before 08:00 local → today's 08:00 is still ahead;
  // at/after 08:00 → today's batch has already fired (the worker runs at the
  // top of the 08:00 hour), so the next one is tomorrow.
  const when = hourInZone(timeZone, now) < 8 ? "today" : "tomorrow";
  return `It's ${localTime} there now — reminders send at 8:00 AM, so the next one goes out ${when}.`;
}
