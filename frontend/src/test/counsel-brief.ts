/**
 * Reader for the counsel gate's §0 register (`docs/rule-engine/G1-COUNSEL-BRIEF.md`).
 *
 * That register is what the attorney engagement is scoped from, and its rows
 * quote the shipped copy so counsel can answer yes/no on the exact wording. A
 * quote that no longer matches the page is worse than no quote: counsel blesses
 * a string nobody ships. So each CLM item that quotes copy carries a test
 * pinning its quotes against what actually renders — CLM-4 in
 * `marketing-claims.test.ts` (#403), CLM-5 in `app/portal/[token]/page.test.tsx`
 * (#404) — and both need the same three mechanical steps: find the file, take
 * the ONE row for the item, pull its quoted sentences out.
 *
 * This module owns those three steps so the next CLM item is one call rather
 * than a fourth transcription of the path walk and the quote regex. The
 * assertions themselves stay with each consumer: what "actually ships" means
 * differs per item (CLM-4 scans source across every surface; CLM-5 renders two
 * portal states, because its notice embeds a `<Link>` mid-sentence and is not
 * contiguous in the file).
 *
 * `src/test/**` is knip-ignored (see `knip.jsonc`) — the shared toolkit is
 * expected to export helpers whether or not every one is wired up right now.
 */
import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";

/** `frontend/src/test` → repo root. */
const REPO_ROOT = resolve(__dirname, "..", "..", "..");

/** The counsel gate. Also a `frontend-ci` path trigger — a brief-only edit must run these pins. */
export const COUNSEL_BRIEF_PATH = join(REPO_ROOT, "docs", "rule-engine", "G1-COUNSEL-BRIEF.md");

/**
 * The register marks copy it wants blessed as `*"…"*` — italicised quotes.
 * Non-blockquote text and backticked evidence from OTHER documents are
 * deliberately not matched: a row can cite the Terms without asking counsel to
 * confirm that the Terms ship on the portal.
 */
const QUOTED_COPY = /\*"(.+?)"\*/g;

export interface CounselRegisterRow {
  /** Every §0 register line for this item. Assert `.length === 1` — two rows means a split edit. */
  readonly rows: readonly string[];
  /** The sentences the row asks counsel to bless, in row order. */
  readonly quoted: readonly string[];
}

/**
 * Read the §0 register row for a CLM item (`"CLM-4"`, `"CLM-5"`, …).
 *
 * Matches on the BOLD form (`| **CLM-5**`), which is what §0 uses — §C's detail
 * table writes the same ids unbolded, so the two never collide and a consumer
 * gets the register row rather than the prose one.
 */
export function counselRegisterRow(item: string): CounselRegisterRow {
  const rows = readFileSync(COUNSEL_BRIEF_PATH, "utf8")
    .split("\n")
    .filter((line) => line.startsWith(`| **${item}**`));
  const quoted = rows.length === 1 ? [...rows[0].matchAll(QUOTED_COPY)].map((m) => m[1]) : [];
  return { rows, quoted };
}

/**
 * Fold a sentence to the one shape comparisons can use: whitespace collapsed,
 * so a quote wrapped across source lines or split by JSX still reads as one
 * phrase against rendered `textContent`.
 */
export function flattenCopy(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}
