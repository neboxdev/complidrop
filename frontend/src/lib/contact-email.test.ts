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
import { dirname, resolve } from "node:path";
import { describe, expect, it } from "vitest";

import {
  CONTACT_EMAIL_ERROR,
  CONTACT_EMAIL_HIDDEN_CHARACTER_ERROR,
  CONTACT_EMAIL_MAX_LENGTH,
  CONTACT_EMAIL_TOO_LONG_ERROR,
  contactEmailError,
  isMalformedContactEmail,
  trimContactEmail,
} from "@/lib/contact-email";

type Cases = {
  maxLength: number;
  blankRanges: [number, number][];
  messages: { invalid: string; hiddenCharacter: string; tooLong: string };
  valid: string[];
  malformed: string[];
  paddedValid: { raw: string; normalized: string }[];
  blank: string[];
};

// Located by walking UP from the working directory rather than resolving a fixed `../` hop.
// cwd is only `frontend/` because the CI job sets working-directory, so a fixed relative path
// throws at module scope — killing the whole file rather than one assertion — when the suite is
// run from the repo root instead. (`import.meta.url` is not usable here: under Vite's transform
// it is not a file: URL, so fileURLToPath rejects it.) Existence is still asserted rather than
// assumed: a silently-missing fixture would make every it.each below vacuous.
const FIXTURE_REL = "api/CompliDrop.Api.Tests/SharedFixtures/contact-email-cases.json";

function locateFixture(): string {
  let dir = process.cwd();
  for (let up = 0; up < 5; up++) {
    const candidate = resolve(dir, FIXTURE_REL);
    if (existsSync(candidate)) return candidate;
    const parent = dirname(dir);
    if (parent === dir) break; // filesystem root
    dir = parent;
  }
  throw new Error(
    `Shared contact-email corpus not found: no ${FIXTURE_REL} above ${process.cwd()}. It is the ` +
      `single source both this suite and ContactEmailTests read (#369) — do not inline the cases ` +
      `here instead.`,
  );
}

const FIXTURE = locateFixture();
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

  it("agrees with the shared corpus about the length cap", () => {
    // The varchar(256) column width was the ONE rule of the mirror pair the corpus did not
    // declare: each side asserted the cap against its OWN constant, so this one and
    // ContactEmail.MaxLength could drift apart with both suites green — and this side would then
    // leave Save enabled on an address the server 400s, which is exactly the form-vs-API drift
    // #369 exists to remove. Declared once in the corpus, asserted on both sides.
    expect(CONTACT_EMAIL_MAX_LENGTH).toBe(cases.maxLength);
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

describe("trimContactEmail stays linear (#369)", () => {
  it("strips a blank-heavy value promptly", () => {
    // Mirror of the server's `Normalization_of_a_blank_heavy_value_completes_promptly`.
    // reviewers.md requires the linear scan on BOTH sides, but only the server had a guard: this
    // file would not have failed if trimContactEmail were rewritten back into the quadratic
    // `^[BLANK]+|[BLANK]+$` regex, because every corpus padding case is short and edge-anchored.
    //
    // The shape is load-bearing. Leading/trailing padding is LINEAR even under the regex (the
    // `^`-anchored alternative consumes it in one match), so an edge-padded probe would be
    // vacuous. The pathological input is blanks in the MIDDLE with a non-blank at BOTH ends,
    // where neither alternative can match and the engine retries at every offset.
    //
    // isMalformedContactEmail runs synchronously on every keystroke and paste in both vendor
    // forms, so the regression this guards is a frozen tab, not just a slow function.
    const hostile = "x" + " ".repeat(500_000) + "x";

    const started = performance.now();
    const result = trimContactEmail(hostile);
    const elapsed = performance.now() - started;

    expect(result).toBe(hostile); // interior blanks are not edges — nothing is stripped
    expect(elapsed).toBeLessThan(1_500);
  });
});

describe("the blank set is owned by the shared corpus (#369)", () => {
  it("implements exactly the corpus's blankRanges across the BMP", () => {
    // The case lists only SAMPLE the class, so cross-language agreement used to be sampled too:
    // adding a range to ONE mirror passed both suites whenever the added code points were not
    // sampled. `blankRanges` declares the SET; both sides walk the BMP against it.
    const inCorpus = new Array<boolean>(0x10000).fill(false);
    for (const [lo, hi] of cases.blankRanges) {
      for (let cp = lo; cp <= hi; cp++) inCorpus[cp] = true;
    }

    const disagreements: string[] = [];
    for (let cp = 0; cp <= 0xffff; cp++) {
      // Probed through the public api: a lone blank-class character strips to nothing.
      const byPredicate = trimContactEmail(String.fromCharCode(cp)) === null;
      if (byPredicate !== inCorpus[cp]) {
        disagreements.push(
          `U+${cp.toString(16).toUpperCase().padStart(4, "0")} (predicate=${byPredicate}, corpus=${inCorpus[cp]})`,
        );
        if (disagreements.length >= 10) break;
      }
    }

    expect(disagreements).toEqual([]);
  });
});

describe("rejection copy (#369)", () => {
  it("matches the shared corpus, so client and server cannot say different things", () => {
    expect(CONTACT_EMAIL_ERROR).toBe(cases.messages.invalid);
    expect(CONTACT_EMAIL_HIDDEN_CHARACTER_ERROR).toBe(cases.messages.hiddenCharacter);
    expect(CONTACT_EMAIL_TOO_LONG_ERROR).toBe(cases.messages.tooLong);
  });

  it("names the invisible-character case instead of the generic typo copy", () => {
    // "Enter a valid contact email address" is unactionable when the field LOOKS correct: a pasted
    // zero-width character renders as nothing, so the user re-reads a correct-looking address with
    // nothing to act on. The explicit blank class is what makes this reachable (JS's \s does not
    // cover ZWSP), so the copy had to arrive with it.
    expect(contactEmailError("ops\u200Bacme@acme.com")).toBe(CONTACT_EMAIL_HIDDEN_CHARACTER_ERROR);
    expect(contactEmailError("ops\u00A0acme@acme.com")).toBe(CONTACT_EMAIL_HIDDEN_CHARACTER_ERROR);

    // A plain typo keeps the generic copy — the hidden-character wording would be a lie.
    expect(contactEmailError("jane@acme,com")).toBe(CONTACT_EMAIL_ERROR);

    // The display-name form is the sharp case: it contains SPACES, which ARE in the blank class,
    // but a space is plainly visible. Calling it a hidden character would send the user hunting for
    // something invisible in a string whose problem is right there in front of them.
    expect(contactEmailError("Jane Smith <jane@acme.com>")).toBe(CONTACT_EMAIL_ERROR);
    expect(contactEmailError("jane doe@acme.com")).toBe(CONTACT_EMAIL_ERROR);

    expect(contactEmailError("a".repeat(250) + "@acme.com")).toBe(CONTACT_EMAIL_TOO_LONG_ERROR);

    expect(contactEmailError("ops@acme.com")).toBeUndefined();
    expect(contactEmailError("   ")).toBeUndefined();
    expect(contactEmailError(null)).toBeUndefined();
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
