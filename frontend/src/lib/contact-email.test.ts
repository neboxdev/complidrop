/**
 * The shared vendor contact-email predicate (#369).
 *
 * Behavior contract pinned here; each form's wiring (Save blocked + inline message) is
 * pinned in that page's own test file.
 *
 * The accept/reject corpus is NOT written inline — it is loaded from
 * `api/CompliDrop.Api.Tests/SharedFixtures/contact-email-cases.json`, the same file that drives
 * `ContactEmailTests.MalformedEmails()` on the server. That is deliberate: the first
 * review pass found that hand-maintained parallel lists were already unequal at
 * introduction, and that the two `\s`-based regexes genuinely disagreed on real input
 * (.NET's `\s` has U+0085 and lacks U+FEFF; JS's is the reverse). Driving both suites from
 * one file makes "these two implementations agree" a mechanical property instead of a
 * comment nobody re-checks.
 */
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

import {
  CONTACT_EMAIL_MAX_LENGTH,
  isMalformedContactEmail,
  trimContactEmail,
} from "@/lib/contact-email";

type Cases = {
  valid: string[];
  malformed: string[];
  paddedValid: { raw: string; normalized: string }[];
  blank: string[];
};

// vitest runs with cwd = frontend/, so the repo root is one level up. Asserted rather than
// assumed: a silently-missing fixture would make every it.each below vacuous.
const FIXTURE = resolve(
  process.cwd(),
  "../api/CompliDrop.Api.Tests/SharedFixtures/contact-email-cases.json",
);
if (!existsSync(FIXTURE)) {
  throw new Error(
    `Shared contact-email corpus not found at ${FIXTURE}. It is the single source both this ` +
      `suite and ContactEmailTests read (#369) — do not inline the cases here instead.`,
  );
}
const cases: Cases = JSON.parse(readFileSync(FIXTURE, "utf8"));

/**
 * Render non-printable / non-ASCII code points as \uXXXX so a failing assertion names the
 * character instead of printing an invisible glyph. Tested by code point rather than a
 * character class, so this helper carries no invisible literals of its own.
 */
const show = (s: string) =>
  [...s]
    .map((c) => {
      const code = c.charCodeAt(0);
      return code >= 0x20 && code <= 0x7e
        ? c
        : `\\u${code.toString(16).padStart(4, "0").toUpperCase()}`;
    })
    .join("");

describe("isMalformedContactEmail (#369)", () => {
  it("loaded a non-trivial shared corpus", () => {
    // Guards the whole file: if the fixture ever failed to load or was emptied, every
    // it.each below would vacuously pass with zero cases.
    expect(cases.valid.length).toBeGreaterThan(3);
    expect(cases.malformed.length).toBeGreaterThan(10);
    expect(cases.paddedValid.length).toBeGreaterThan(3);
    expect(cases.blank.length).toBeGreaterThan(3);
  });

  it.each(cases.malformed)("rejects %s", (bad) => {
    expect(isMalformedContactEmail(bad), `${show(bad)} must be rejected`).toBe(true);
  });

  it.each(cases.valid)("accepts %s", (good) => {
    expect(isMalformedContactEmail(good), `${show(good)} must be accepted`).toBe(false);
  });

  it.each(cases.blank)("treats blank %#) as valid — a vendor with no contact email is a supported state", (blank) => {
    // Load-bearing: if blank were "malformed", Save would be permanently disabled on every
    // vendor that has no contact email.
    expect(isMalformedContactEmail(blank), `${show(blank)} must be treated as absent`).toBe(false);
  });

  it("treats null and undefined as valid", () => {
    expect(isMalformedContactEmail(null)).toBe(false);
    expect(isMalformedContactEmail(undefined)).toBe(false);
  });

  it.each(cases.paddedValid)("accepts padded $raw once stripped", ({ raw }) => {
    expect(isMalformedContactEmail(raw), `${show(raw)} must be accepted after stripping`).toBe(false);
  });

  it("rejects an address longer than the varchar(256) column", () => {
    // Npgsql does NOT truncate — without this cap the write 500s instead of 400ing.
    const domain = "@acme.com";
    const atLimit = "a".repeat(CONTACT_EMAIL_MAX_LENGTH - domain.length) + domain;
    expect(atLimit).toHaveLength(CONTACT_EMAIL_MAX_LENGTH);
    expect(isMalformedContactEmail(atLimit)).toBe(false);
    expect(isMalformedContactEmail("a" + atLimit)).toBe(true);
  });
});

describe("the blank class and the blank predicate agree (#369)", () => {
  it("never disagrees on any code point in the BMP", () => {
    // The blank set exists TWICE in this module by necessity: as the BLANK character class
    // (used by WELL_FORMED to REJECT) and as the isBlank predicate (used by trimContactEmail
    // to STRIP). A range added to one and not the other splits the two halves of one rule —
    // an address could be rejected as malformed while its padding was left unstripped, or a
    // padded address could strip to something the server then rejects. Either way the mirrors
    // stop agreeing with `Services/ContactEmail.cs`, which is the failure #369 is about.
    //
    // Both sides are probed through the PUBLIC api rather than by exporting the constant, so
    // this pins observable behavior:
    //   class     — a blank char mid-address makes it malformed (WELL_FORMED excludes BLANK)
    //   predicate — a lone blank char strips to nothing, i.e. normalizes to null
    //
    // Mirrors `The_blank_predicate_and_the_character_class_agree` on the server, which walks
    // the same range against the same set.
    const disagreements: string[] = [];

    for (let cp = 0; cp <= 0xffff; cp++) {
      const c = String.fromCharCode(cp);
      if (c === "@") continue; // malformed for an unrelated reason — not a blank-class signal

      const byClass = isMalformedContactEmail(`a${c}b@acme.com`);
      const byPredicate = trimContactEmail(c) === null;

      if (byClass !== byPredicate) {
        disagreements.push(
          `U+${cp.toString(16).toUpperCase().padStart(4, "0")} (class=${byClass}, predicate=${byPredicate})`,
        );
        if (disagreements.length >= 10) break;
      }
    }

    expect(disagreements).toEqual([]);
  });
});

describe("trimContactEmail (#369)", () => {
  it.each(cases.paddedValid)("strips $raw to its bare address", ({ raw, normalized }) => {
    expect(trimContactEmail(raw)).toBe(normalized);
  });

  it.each(cases.blank)("collapses blank %#) to null", (blank) => {
    expect(trimContactEmail(blank)).toBeNull();
  });

  it("collapses null and undefined to null", () => {
    expect(trimContactEmail(null)).toBeNull();
    expect(trimContactEmail(undefined)).toBeNull();
  });
});
