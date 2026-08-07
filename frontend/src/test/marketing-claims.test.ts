/**
 * Census: no shipped surface may carry a claim the Terms disclaim (#403).
 *
 * Why a census rather than more per-page assertions. #403 fixed the same
 * sentence shape on eight surfaces, and every fix was pinned by a negative
 * regex scoped to the page it happened to touch — so the guard covered exactly
 * the pages someone had already thought about. Round 2 of the review then found
 * two more live instances of the identical claim (an untouched paragraph on the
 * event-venues page, and the repo README) that no assertion could ever have
 * caught. This is the `ExportDisclaimerTests` shape from the backend, which
 * exists for the same reason: a behavioural test can only pin the surfaces it
 * renders, so the whole-file source scan is what covers the ones nobody
 * remembered.
 *
 * The input is `docs/rule-engine/G1-LEGAL-RESEARCH.md` §V.4 ("Marketing claim
 * rules — Never: 'ensures/guarantees compliance,' 'keeps you compliant,' 'so
 * you're always covered,' … any claim of completeness") plus the specific
 * sentences #403 retired, each of which is falsifiable against code in this
 * repo. Every rule carries its `why` so a failure explains itself, and its
 * `sample` — the actual retired copy where there is one — so the matcher can't
 * quietly stop matching.
 *
 * SCOPE NOTE: this scans SOURCE, so it is a backstop, not a proof. Copy
 * assembled at runtime from fragments can evade it; the per-surface rendered
 * assertions in `marketing-content.test.tsx` and `page.test.tsx` remain the
 * primary pins for the REPLACEMENT wording. This one only answers "did a banned
 * claim come back anywhere".
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, it, expect } from "vitest";
import { counselRegisterRow } from "./counsel-brief";

const FRONTEND_SRC = resolve(__dirname, "..");
const REPO_ROOT = resolve(__dirname, "..", "..", "..");
const REPO_README = join(REPO_ROOT, "README.md");
const TEST_FILE_RE = /\.(test|spec)\.tsx?$/;

interface ClaimRule {
  /** Matched against normalized source (see `normalize`). Never global — `lastIndex` state would make results order-dependent. */
  readonly pattern: RegExp;
  /** Why the claim is not ours to make. Printed on failure. */
  readonly why: string;
  /** Copy this rule must catch — the real retired sentence where one exists. Pinned by the matcher self-test below. */
  readonly sample: string;
  /**
   * Further copy the rule must catch — the near-synonyms it was broadened to
   * cover, none of which was ever shipped, so none can be a `sample`.
   *
   * Added by round 2 of the #398 review (S7). The four deletion rules matched
   * only the exact sentences #398 retired, while the records claimed the census
   * enforced the whole invariant ("not 'permanently', not 'can't be undone',
   * not 'we delete your data'") — so "This permanently REMOVES the vendor and
   * everything they sent." passed. Broadening a pattern without pinning what
   * the broadening buys is how a rule quietly narrows again on the next edit;
   * this is the `sample` discipline applied to the family rather than to the
   * one historical string.
   */
  readonly alsoCatches?: readonly string[];
}

const TERMS_REMINDERS =
  "the Terms disclaim reminder delivery (\"a helpful nudge, not a guaranteed notice\"); a document whose expiration date was never extracted matches no reminder window at all, and a suppressed recipient is skipped (ADR 0031)";
const TERMS_ACCURACY =
  "the Terms disclaim accuracy (\"we do not guarantee that every extracted value or compliance result is accurate or complete\")";
const NEVER_SAY = "G1-LEGAL-RESEARCH §V.4 never-say list";
const TERMS_DELETION =
  "nothing in CompliDrop hard-deletes (#398 / ADR 0013 Amendment 1): closing an account scrubs the holder's email + name and soft-deletes the user + org, while the vendors' contact details, the documents, the uploaded blobs, the reminder logs, the Subscription row and the audit trail are all RETAINED";

const BANNED_CLAIMS: readonly ClaimRule[] = [
  // ── §V.4 "Never" ──────────────────────────────────────────────────────────
  {
    pattern: /(ensures|guarantees) compliance/i,
    why: `${NEVER_SAY}: compliance is the customer's, and ${TERMS_ACCURACY}`,
    sample: "CompliDrop ensures compliance across your whole vendor list.",
  },
  {
    pattern: /keeps you compliant/i,
    why: `${NEVER_SAY}, verbatim`,
    sample: "The dashboard keeps you compliant all year.",
  },
  {
    pattern: /always covered/i,
    why: `${NEVER_SAY}: coverage depends on the vendor's policy, which we only read`,
    sample: "Track every cert so you're always covered.",
  },
  {
    pattern: /never miss (an?|another|every)?\s*(expir|renewal|certificate|deadline)/i,
    why: `a claim of completeness (${NEVER_SAY}); ${TERMS_REMINDERS}`,
    sample: "Never miss another expiration.",
  },
  {
    pattern: /nothing slips/i,
    why: `a claim of completeness (${NEVER_SAY}); ${TERMS_REMINDERS}`,
    sample: "Nothing slips past CompliDrop.",
  },
  {
    pattern: /100% (compliant|accurate)/i,
    why: `a claim of completeness (${NEVER_SAY}); ${TERMS_ACCURACY}`,
    sample: "100% accurate extraction.",
  },
  // ── The sentences #403 retired, each falsifiable against this repo ─────────
  {
    pattern: /sell or share/i,
    why: "the Privacy Policy's own \"Service providers we share data with\" section lists nine vendors, document contents among them — say what CCPA \"sharing\" means instead",
    sample: "We don't sell or share your data.",
  },
  {
    pattern: /(don't|do not|never) shares? your data/i,
    why: "same contradiction as \"sell or share\" — we do share, with disclosed subprocessors",
    sample: "We never share your data with anyone.",
  },
  {
    pattern: /can't slip through/i,
    why: `${TERMS_REMINDERS}. ("won't slip through unnoticed" is the shipped replacement and is deliberately NOT banned — the dashboard notices an expiry independently of email; it is routed to counsel as a CLM-4 question)`,
    sample: "A missed expiration can't slip through a row you forgot to check.",
  },
  {
    pattern: /before anything (expires|lapses)/i,
    why: `${TERMS_REMINDERS} — say "ahead of the expiration date"`,
    sample:
      "SMB compliance-document tracking SaaS — drop a COI / license / permit, extract the fields, get warned before anything expires.",
  },
  {
    pattern: /warns? you (before|—|-|and)/i,
    why: `${TERMS_REMINDERS}; "warn" also asserts the notice arrived`,
    sample: "CompliDrop reads the dates and warns you — and your vendor — before anything expires.",
  },
  {
    pattern: /never the reason a booking/i,
    why: "whether a booking survives is not something CompliDrop controls",
    sample: "…so a missing COI is never the reason a booking falls apart.",
  },
  {
    pattern: /never holds up a booking/i,
    why: "same outcome guarantee as \"never the reason a booking\", in the meta description",
    sample: "…so a missing certificate never holds up a booking.",
  },
  {
    pattern: /renews your vendors/i,
    why: "CompliDrop renews nothing — the reminder emails the vendor an upload link and the VENDOR acts (ReminderBackgroundService.BuildVendorBody); there is no carrier integration in the API",
    sample: "CompliDrop renews your vendors' certificates automatically.",
  },
  {
    pattern: /enterprise accuracy/i,
    why: TERMS_ACCURACY,
    sample: "Enterprise accuracy, small-business price.",
  },
  {
    pattern: /looking at a clean list/i,
    why: `whether the list is clean depends on the vendor uploading, which CompliDrop does not control; ${TERMS_REMINDERS}`,
    sample:
      "Reminders go out automatically before a certificate expires, so by the day of the event you're looking at a clean list, not a phone in your hand.",
  },
  // ── The deletion-claim family #398 retired (CLM-7) ─────────────────────────
  // Nothing in CompliDrop hard-deletes. Account closure scrubs the holder's
  // email + name and stamps `DeletedAt`; `DeleteDocument` / `DeleteVendor`
  // soft-delete and the document's blob is RETAINED on purpose. These belong in
  // the census rather than in per-page assertions for the reason the census
  // exists: the same sentence sat on four surfaces (settings card, settings
  // form, document list row, document detail header) and the fifth — a new
  // dialog on a page nobody has written yet — is the one an assertion cannot
  // reach. "Deceptive deletion" is also the finding class with the most
  // regulatory teeth, so it is the last claim that should depend on someone
  // remembering.
  {
    // Broadened past the literal verb (#398 round 2 / S7): the claim is
    // "permanently" + ANY erasure word, and "permanently removes" is the phrase
    // a new dialog reaches for. Noun forms ride along via `permanent(ly)?`.
    pattern: /permanent(ly)?\s+(delet|remov|eras|destroy|wip|purg)/i,
    why: `${TERMS_DELETION}; ADR 0013 also names support-reversibility as a benefit, so "permanently" is false twice over`,
    sample: "Permanently deletes your account and organization data. This can't be undone.",
    alsoCatches: [
      "This permanently removes the vendor and everything they sent.",
      "Closing your account triggers permanent deletion of your files.",
      "Confirm to permanently erase this certificate.",
    ],
  },
  {
    // The irreversibility claim is a FAMILY, not the one literal #398 happened
    // to retire (S7). Bare "forever" is deliberately NOT here — "Free forever",
    // "tracked forever" and "Locked forever" are shipped, true, and about
    // something else entirely.
    pattern:
      /\b(irreversibl[ey]|can(no|')t be reversed|cannot be reversed|deleted forever|gone forever|unrecoverable|permanent(ly)? and final)\b/i,
    why: `${TERMS_DELETION} — nothing is irreversible: the row keeps its DeletedAt tombstone, a document keeps its blob, and support restores an account by clearing DeletedAt`,
    sample: "Removing a vendor is irreversible.",
    alsoCatches: [
      "Once you confirm, this action cannot be reversed.",
      "Your uploads are gone forever.",
    ],
  },
  {
    pattern: /can(no|')t be undone/i,
    why: `${TERMS_DELETION} — every delete path soft-deletes, and DeleteDocument keeps the blob so the document "remains recoverable" (its own comment). Say what the CUSTOMER can't do instead`,
    sample: "This removes the document from your records and can't be undone.",
  },
  {
    // The third phrase frontend/CLAUDE.md bans and no rule matched (S7): the
    // plain first-person erasure promise. Scoped to "we <verb> your/all/every…"
    // so the Privacy Policy's honest "…until you ask us to delete them" and
    // "delete what we can" (a REQUEST channel, not a standing promise) stay legal.
    pattern:
      /\bwe\s+(then\s+|also\s+|automatically\s+|permanently\s+)?(delete|erase|destroy|wipe|purge)\s+(your|all|every|everything)\b/i,
    why: `${TERMS_DELETION}. We delete nothing on closure — say what closure DOES (scrub the holder's name + email, cancel the plan, stop reminders) and what is kept`,
    sample: "When you close your account we delete your data.",
    alsoCatches: ["We permanently erase all documents you uploaded."],
  },
  {
    // Same claim in the passive, which is how a policy page phrases it.
    pattern:
      /\byour\s+(data|documents|files|account|information|uploads)\s+(is|are|will be|gets?)\s+(permanently\s+|automatically\s+)?(deleted|erased|destroyed|wiped|purged)\b/i,
    why: `${TERMS_DELETION} — the passive voice does not make it true; no purge job exists anywhere in the codebase`,
    sample: "Your data is permanently deleted when you close your account.",
    alsoCatches: ["Your documents will be erased once the account closes."],
  },
  {
    pattern: /(delete|de-identify)[^.]{0,40}within a reasonable/i,
    why: "the retired Privacy Policy retention promise — no purge job exists anywhere in the codebase, so nothing implements it (#398)",
    sample:
      "If you close your account, we delete or de-identify your data within a reasonable period.",
  },
  {
    pattern: /\b(delete|deleted|dispose of|disposed of|purge)\b[^.]{0,60}\b(after|within)\s+\d{1,3}\s*(day|month|year)/i,
    why: "a retention SCHEDULE nobody enforces recreates #398's defect in a new sentence — the disposal question is counsel-gate CLM-7, not a copy decision",
    sample: "If you close your account we delete everything you uploaded within 90 days.",
  },
];

/**
 * Fold the shapes the same sentence takes across .tsx / .ts / .md into one
 * comparable string: JSX entities and curly punctuation back to ASCII, and all
 * whitespace to single spaces so a claim wrapped across source lines still
 * reads as one phrase.
 */
function normalize(source: string): string {
  return source
    .replace(/&rsquo;|&lsquo;|&apos;|&#39;|[‘’]/g, "'")
    .replace(/&mdash;|&ndash;|&nbsp;/g, " ")
    .replace(/&quot;|[“”]/g, '"')
    .replace(/\s+/g, " ");
}

/**
 * Drop comments before scanning. A source comment that quotes a banned claim in
 * order to explain the ban (this file is full of them) must not read as the
 * claim shipping. The `//` strip requires the slashes not to be preceded by
 * `:` so `https://…` survives intact and can't hide text after it.
 */
function stripComments(source: string): string {
  return source.replace(/\/\*[\s\S]*?\*\//g, " ").replace(/(^|[^:])\/\/[^\n]*/g, "$1");
}

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      if (entry === "node_modules" || entry === ".next") continue;
      walk(full, out);
    } else if (st.isFile() && (full.endsWith(".ts") || full.endsWith(".tsx"))) {
      if (!TEST_FILE_RE.test(full)) out.push(full);
    }
  }
  return out;
}

/** Every shipped frontend module, plus the repo README — the public one-liner that survived both #403 sweeps. */
const SURFACES: ReadonlyArray<readonly [label: string, path: string]> = [
  ...walk(FRONTEND_SRC).map(
    (file) => [relative(FRONTEND_SRC, file).replace(/\\/g, "/"), file] as const,
  ),
  ["../../README.md", REPO_README] as const,
];

describe("Marketing-claim census (#403)", () => {
  it("every rule matches the copy it exists to ban (the matcher is not dark)", () => {
    // Without this the whole census could silently stop matching — a broken
    // `normalize`, an over-escaped pattern, an entity form nobody anticipated —
    // and every file below would pass for the wrong reason. `alsoCatches` extends
    // the same discipline to the near-synonyms a broadened rule was widened FOR
    // (#398 round 2 / S7): a pattern narrowed back to its historical string goes
    // red here rather than going quietly dark on copy nobody has written yet.
    const dark = BANNED_CLAIMS.flatMap((rule) =>
      [rule.sample, ...(rule.alsoCatches ?? [])]
        .filter((copy) => !rule.pattern.test(normalize(copy)))
        .map((copy) => `${String(rule.pattern)} misses: ${copy}`),
    );
    expect(dark).toEqual([]);
    // Floor raised from 15 by #398's four deletion rules, then to 22 by round 2's
    // three (the irreversibility family, "we delete your…", and its passive) — a
    // rule deleted rather than deliberately retired reddens here.
    expect(BANNED_CLAIMS.length).toBeGreaterThanOrEqual(22);
  });

  it("scans the whole shipped surface, not a hand-picked subset", () => {
    const labels = SURFACES.map(([label]) => label);
    expect(labels.length).toBeGreaterThan(50);
    // The surfaces #403 actually corrected, so a walk that stopped reaching
    // them fails here rather than passing vacuously.
    expect(labels).toContain("app/page.tsx");
    expect(labels).toContain("app/faq/page.tsx");
    expect(labels).toContain("app/coi-tracking-for-event-venues/page.tsx");
    expect(labels).toContain("components/marketing/content.tsx");
    expect(labels).toContain("components/onboarding/WelcomeModal.tsx");
    expect(labels).toContain("lib/site.ts");
    expect(labels).toContain("../../README.md");
  });

  it.each(SURFACES)("%s carries no claim the Terms disclaim", (label, path) => {
    const text = normalize(stripComments(readFileSync(path, "utf8")));
    const hits = BANNED_CLAIMS.filter((rule) => rule.pattern.test(text)).map(
      (rule) => `${String(rule.pattern)} — ${rule.why}`,
    );
    expect(hits, `${label} ships a banned claim`).toEqual([]);
  });
});

/**
 * The counsel gate's §0 register is what the attorney engagement is scoped
 * from, and its CLM-4 row quotes the copy #403 shipped so counsel can answer
 * yes/no on the exact wording. A quote that no longer matches the page is worse
 * than no quote: counsel blesses a string nobody ships. This is also the guard
 * that keeps the register honest when the §C detail row is edited alone — the
 * defect that put the footer tagline in one row and not the other.
 */
/**
 * Every shipped surface with its comments stripped — so a register quote that
 * survives only inside a code comment explaining why it was retired does NOT
 * read as shipping. (The CLM-4 pin scanned raw source; nothing depended on the
 * looseness, and #398's corrections are quoted in comments beside the copy they
 * replaced, which is exactly the vacuous pass this closes.)
 */
const SHIPPED_COPY = SURFACES.map(([, path]) => normalize(stripComments(readFileSync(path, "utf8"))));

/**
 * Assert a §0 register row is single, quotes at least `minQuotes` sentences,
 * and that each of them is copy some surface actually carries.
 *
 * Shared because CLM-4 and CLM-7 want the identical three assertions, and the
 * next CLM item will too — the same reason `./counsel-brief` owns the path walk
 * and the quote regex (#404 review S6).
 */
function pinRegisterQuotes(
  item: string,
  minQuotes: number,
  whatIsQuoted: string,
  /**
   * Quotes this SOURCE scan structurally cannot see, because the copy embeds a
   * `<Link>` mid-sentence and is therefore not contiguous in the file — only a
   * render puts the link's own text back inline. Each entry names the test that
   * pins it instead, and the prefix is asserted to still match one of the row's
   * quotes, so a register reword cannot silently orphan the exemption. This is
   * the same split CLM-5 has lived on since #404 (`app/portal/[token]/page.test.tsx`
   * renders two portal states for exactly this reason); #398 round 2 brought
   * CLM-7 (a) into it by linking the Privacy Policy the sentence defers to.
   */
  pinnedByARender: ReadonlyArray<readonly [prefix: string, pinnedBy: string]> = [],
): void {
  const { rows: row, quoted } = counselRegisterRow(item);

  it(`§0 ${item} is a single row that quotes every item it asks counsel to bless`, () => {
    expect(row, `expected exactly one §0 ${item} register row`).toHaveLength(1);
    expect(quoted.length, whatIsQuoted).toBeGreaterThanOrEqual(minQuotes);
    for (const [prefix, pinnedBy] of pinnedByARender) {
      expect(
        quoted.filter((q) => q.includes(prefix)),
        `the §0 ${item} row no longer quotes the sentence ${pinnedBy} pins — either the register was reworded (update the exemption) or the quote was dropped`,
      ).toHaveLength(1);
    }
  });

  const scannable = quoted.filter(
    (q) => !pinnedByARender.some(([prefix]) => q.includes(prefix)),
  );

  it.each(scannable.map((q) => [q.slice(0, 56), q] as const))(
    `${item}: the copy it quotes as "%s…" actually ships`,
    (_label, sentence) => {
      const where = SHIPPED_COPY.filter((text) => text.includes(normalize(sentence)));
      expect(
        where.length,
        `the §0 ${item} row quotes copy that no shipped surface carries — either the page was reworded without updating the brief, or the brief quotes something that was never shipped:\n  ${sentence}`,
      ).toBeGreaterThan(0);
    },
  );
}

describe("Counsel brief §0 CLM-4 register (#403)", () => {
  // (a) the FAQ answer, (b) the CCPA parenthetical, (c) the footer tagline,
  // (d) the "won't slip through unnoticed" clause, (e) the Rules page's "warn
  // you 30 days before" helper — added by #398 as a QUESTION, not a rewrite:
  // it is true of the code (60/30/14/7 seeded reminders; ExpiringSoonWindowDays
  // = 30) but it is (d)'s delivery-flavoured family and both #403 clearance
  // reviewers named it. Dropping one must be a deliberate edit, not an accident
  // of rewriting the cell.
  pinRegisterQuotes("CLM-4", 5, "expected (a)–(e) to be quoted in the CLM-4 row");
});

/**
 * The deletion claim is the highest-risk sentence in the product, so the §0 row
 * that scopes counsel's engagement has to quote what actually renders. Same
 * guard as CLM-4's, aimed at the copy #398 shipped: (a) the Settings closure
 * notice, (b) the Privacy Policy's replacement retention sentences, (c) the
 * per-item removal notice, (d) the export description.
 */
describe("Counsel brief §0 CLM-7 register (#398)", () => {
  pinRegisterQuotes("CLM-7", 6, "expected (a)–(d) — six sentences — to be quoted in the CLM-7 row", [
    [
      "we handle them as described in our Privacy Policy.",
      "`app/(dashboard)/settings/account-management.test.tsx`",
    ],
  ]);
});
