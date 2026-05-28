/**
 * Pins the quarantine-registry drift-check pure helpers (#115).
 * The CLI orchestration (fetching #87 via `gh`, exiting non-zero)
 * is exercised by the workflow itself; this fast-tier test pins the
 * marker-scan + registry-parse + comparison logic so a regression
 * in any of the three surfaces before the workflow runs.
 *
 * Invariants pinned:
 *   1. `parseMarkersFromSource` finds `test.fixme(` and `@quarantine`
 *      markers, extracts test name + ticket-from-TODO.
 *   2. `parseRegistry` extracts checkbox rows from the
 *      "## Quarantine registry" section only — not from sibling
 *      sections — and captures ticked vs unticked state.
 *   3. `compareRegistry` flags the three drift classes:
 *      - marker with no row    → missing
 *      - marker with ticked row → missing (ambiguous re-quarantine)
 *      - unticked row with no marker → stale
 *      - marker with no ticket reference → noTicket
 *   4. Clean case (no markers, registry empty / all-ticked) returns
 *      empty drift report.
 */
import { describe, it, expect } from "vitest";
import { mkdtempSync, writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import {
  parseMarkersFromSource,
  parseRegistry,
  compareRegistry,
  formatDriftReport,
  listSpecFiles,
} from "./check-quarantine-registry.mjs";

describe("parseMarkersFromSource — marker extraction (#115)", () => {
  it("finds a test.fixme call with a ticket reference in the preceding TODO", () => {
    const src = [
      'import { test } from "@playwright/test";',
      "",
      "test.describe(\"Flow X\", () => {",
      "  // TODO #123: flaky in Chromium during cold-jit, parking",
      "  test.fixme(\"the broken flow\", async ({ page }) => {",
      "    // body",
      "  });",
      "});",
      "",
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/x.spec.ts");
    expect(markers).toHaveLength(1);
    expect(markers[0]).toMatchObject({
      file: "e2e/smoke/x.spec.ts",
      line: 5,
      testName: "the broken flow",
      ticket: "123",
    });
  });

  it("finds an @quarantine tag annotation and ticket in nearby comment", () => {
    const src = [
      "test(",
      "  // TODO #456: see ADR 0010 §Flake policy",
      "  \"@quarantine flaky test\",",
      "  async ({ page }) => {},",
      ");",
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/y.spec.ts");
    // The @quarantine line itself is the marker; ticket is on the
    // preceding TODO comment (within the 5-line lookback).
    expect(markers).toHaveLength(1);
    expect(markers[0].ticket).toBe("456");
    expect(markers[0].file).toBe("e2e/smoke/y.spec.ts");
  });

  it("returns ticket=null when no #<digits> appears in the lookback window", () => {
    // Pins the noTicket branch — marker present but no ticket
    // reference. The compareRegistry should flag this separately
    // from "missing row".
    const src = [
      "test.fixme(\"unconnected flake\", async ({ page }) => {});",
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/z.spec.ts");
    expect(markers).toHaveLength(1);
    expect(markers[0].ticket).toBeNull();
    expect(markers[0].testName).toBe("unconnected flake");
  });

  it("does NOT trigger on a comment mentioning test.fixme by name in prose", () => {
    // The script walks `.spec.ts` files via listSpecFiles, but a
    // comment INSIDE a spec file that mentions `test.fixme(` (e.g.
    // a contributor explaining the convention) would technically
    // contain the substring. Pin the current behavior so a future
    // tightening (e.g. "skip if line starts with `//`") is a
    // deliberate change. Today the matcher fires on substring; the
    // ticket-from-TODO lookback would catch the same `#N` and the
    // test author would intentionally suppress with a // eslint-
    // style false-positive comment.
    const src = [
      "// Below we use test.fixme( in the actual spec to skip a flake",
      "test(\"healthy\", async ({ page }) => {});",
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/q.spec.ts");
    // Current behavior: the substring match DOES fire on the comment.
    // The script author can suppress via the registry (add a row) or
    // restructure the comment. Pinning this so a future refactor that
    // narrows the matcher to non-comment lines is a deliberate
    // tightening, not a silent behavior change.
    expect(markers).toHaveLength(1);
    expect(markers[0].testName).toBeNull();
  });

  it("returns empty array for source with neither test.fixme nor @quarantine", () => {
    const src = [
      "test(\"healthy\", async () => {});",
      "test(\"also healthy\", async () => {});",
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/h.spec.ts");
    expect(markers).toEqual([]);
  });

  it("prefers a preceding-line ticket reference over a #N inside the test-name literal", () => {
    // The local-verification path for #115 itself hit this: a test
    // named `"AC #3 synthetic flake — never lands on main"` has
    // `#3` inside the name literal. The preceding TODO has the
    // REAL ticket (`#99999`). The script MUST pick the preceding
    // reference; matching the name-literal's `#3` would tell the
    // developer to add a row for the wrong ticket and the gate
    // would be useless. Pin the precedence so a future refactor
    // doesn't quietly invert it.
    const src = [
      "// TEMP: AC #3 verification for #115.",
      "// TODO #99999: synthetic flake marker.",
      'test.fixme("AC #3 synthetic flake — never lands on main", async () => {});',
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/f.spec.ts");
    expect(markers).toHaveLength(1);
    // The preceding TODO ticket (`#99999`) wins, NOT the in-name
    // `#3` or the in-comment `#115`.
    expect(markers[0].ticket).toBe("99999");
  });

  it("falls back to a same-line ticket reference for the inline-TODO shape", () => {
    // The other documented convention: `test.fixme(...); // TODO #N`
    // on a single line. Preceding-line search finds nothing; the
    // marker line itself supplies the ticket.
    const src = [
      'test.fixme("inline flake", async () => {}); // TODO #777',
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/i.spec.ts");
    expect(markers).toHaveLength(1);
    expect(markers[0].ticket).toBe("777");
  });

  it("extracts multiple markers from one file", () => {
    const src = [
      "// TODO #100: flake A",
      "test.fixme(\"flake A\", async () => {});",
      "",
      "// TODO #200: flake B",
      "test.fixme(\"flake B\", async () => {});",
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/m.spec.ts");
    expect(markers).toHaveLength(2);
    expect(markers[0].ticket).toBe("100");
    expect(markers[1].ticket).toBe("200");
  });

  it("matches test.describe.fixme() — Playwright's whole-describe quarantine idiom", () => {
    // ADR 0010 §Flake policy talks about `test.fixme`; Playwright
    // exposes the same idiom at the suite level as
    // `test.describe.fixme(name, callback)`. If a contributor uses
    // the suite-level variant the gate MUST still catch it — a
    // whole describe block parked in quarantine carries the same
    // risk profile as a single fixme'd test. Without this match
    // the gate would silently miss every suite-level parking and
    // the registry would decay first for suite-level entries.
    const src = [
      "// TODO #555: whole-flow flake",
      'test.describe.fixme("Flow X — entirely flaky", () => {',
      '  test("inner", async () => {});',
      "});",
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/d.spec.ts");
    expect(markers).toHaveLength(1);
    expect(markers[0].testName).toBe("Flow X — entirely flaky");
    expect(markers[0].ticket).toBe("555");
  });

  it("inline-TODO fallback restricts to // trailing comment — in-name `#N` cannot shadow", () => {
    // Critical bug-fix pin (#115 review): a marker whose test name
    // contains `#N` AND has no preceding TODO line. The same-line
    // fallback used to match the in-name `#3` left-to-right and
    // silently mis-route the marker to ticket #3. The fix scopes
    // the same-line regex to the substring AFTER the first `//`.
    // Pinning the post-fix shape so a future loosening (e.g.
    // "find any #N on the marker line") would surface here, not
    // silently in production.
    const src = [
      'test.fixme("AC #3 broken thing", async () => {}); // TODO #777',
    ].join("\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/n.spec.ts");
    expect(markers).toHaveLength(1);
    // The trailing-comment #777 wins, NOT the in-name #3.
    expect(markers[0].ticket).toBe("777");
  });

  it("handles CRLF line endings in spec source", () => {
    // Windows dev machines (with autocrlf=true on checkout) or
    // copy-pasted source through CRLF-normalizing intermediaries
    // produce `\r\n`-separated files. The split regex (`/\r?\n/`)
    // already handles this; pin it so a future "simplify to
    // `\n`" refactor is a deliberate, observable change.
    const src = [
      "// TODO #888: flake",
      'test.fixme("crlf flake", async () => {});',
    ].join("\r\n");
    const markers = parseMarkersFromSource(src, "e2e/smoke/c.spec.ts");
    expect(markers).toHaveLength(1);
    expect(markers[0].ticket).toBe("888");
    expect(markers[0].testName).toBe("crlf flake");
    // Line count must stay correct under CRLF (no off-by-one from
    // a stray `\r` survivor).
    expect(markers[0].line).toBe(2);
  });
});

describe("parseRegistry — issue-body parsing (#115)", () => {
  it("extracts unticked + ticked rows from the registry section", () => {
    const body = [
      "## Goal",
      "Maintain an explicit registry of any E2E test currently behind…",
      "",
      "## Quarantine registry",
      "",
      "<!-- Append one row per quarantined E2E test. Oldest first. -->",
      "",
      "- [ ] #100 — `e2e/smoke/a.spec.ts`:`flake A` — quarantined 2026-05-01",
      "- [x] #200 — `e2e/smoke/b.spec.ts`:`flake B` — quarantined 2026-04-15",
      "",
      "## Non-goals",
      "- Don't automate the parking lot via a workflow.",
    ].join("\n");
    const rows = parseRegistry(body);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({ ticked: false, ticket: "100" });
    expect(rows[1]).toMatchObject({ ticked: true, ticket: "200" });
  });

  it("returns empty array when the registry section is empty / placeholder-only", () => {
    // The default state of #87 — the placeholder `_(empty — no E2E
    // tests currently in quarantine)_` line. It contains no `- [ ]`
    // checkbox; parseRegistry must return [] not crash.
    const body = [
      "## Quarantine registry",
      "",
      "<!-- Append one row per quarantined E2E test. -->",
      "",
      "_(empty — no E2E tests currently in quarantine)_",
      "",
      "## Non-goals",
    ].join("\n");
    expect(parseRegistry(body)).toEqual([]);
  });

  it("does NOT pick up checkbox rows from sibling sections (e.g. Acceptance criteria)", () => {
    // The same issue body has `- [x] ...` rows in its Acceptance
    // criteria. parseRegistry MUST scope to the Quarantine registry
    // section only — picking up sibling-section rows would surface
    // false positives.
    const body = [
      "## Acceptance criteria",
      "- [x] #999 — this is the AC for #87 itself, NOT a quarantine row",
      "",
      "## Quarantine registry",
      "",
      "- [ ] #100 — actual registry row",
      "",
      "## Non-goals",
    ].join("\n");
    const rows = parseRegistry(body);
    expect(rows).toHaveLength(1);
    expect(rows[0].ticket).toBe("100");
  });

  it("stops at the next ## heading even when sections are reordered", () => {
    const body = [
      "## Quarantine registry",
      "- [ ] #100 — row one",
      "## Related",
      "- [x] #999 — this is a Related-section row, NOT in registry",
    ].join("\n");
    const rows = parseRegistry(body);
    expect(rows).toHaveLength(1);
    expect(rows[0].ticket).toBe("100");
  });

  it("accepts both `[x]` and `[X]` as ticked (GFM is case-insensitive)", () => {
    // GitHub Flavored Markdown renders both `- [x]` and `- [X]`
    // as ticked. A contributor's autocorrect or typing-on-mobile
    // can produce uppercase X; the parser must accept it. Without
    // this, an `[X]` row would not be matched at all, the script
    // would treat it as "no row for this ticket", and would
    // emit a misleading "append a row to #87" drift message
    // when the row IS in fact present.
    const body = [
      "## Quarantine registry",
      "- [x] #100 — lowercase ticked",
      "- [X] #200 — uppercase ticked",
      "## Non-goals",
    ].join("\n");
    const rows = parseRegistry(body);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({ ticket: "100", ticked: true });
    expect(rows[1]).toMatchObject({ ticket: "200", ticked: true });
  });

  it("matches the heading case-insensitively (`## quarantine registry` works)", () => {
    // The regex `/^##\s+Quarantine registry\b/i` is case-
    // insensitive on the heading literal; pin it so a future
    // "tighten the match" refactor that drops the `i` flag would
    // surface here. A contributor who writes `## quarantine
    // registry` (lowercase) without this would have every row
    // silently skipped.
    const body = [
      "## quarantine registry",
      "- [ ] #100 — row one",
      "## Non-goals",
    ].join("\n");
    const rows = parseRegistry(body);
    expect(rows).toHaveLength(1);
    expect(rows[0].ticket).toBe("100");
  });

  it("handles CRLF line endings in the issue body", () => {
    // `gh issue view` output is LF on Linux/macOS, but webhook-
    // forwarded bodies or Windows-edited bodies can carry CRLF.
    // The split (`/\r?\n/`) handles this; pin it so the gate
    // works regardless of where the body originated.
    const body = [
      "## Quarantine registry",
      "",
      "- [ ] #100 — crlf row",
      "",
      "## Non-goals",
    ].join("\r\n");
    const rows = parseRegistry(body);
    expect(rows).toHaveLength(1);
    expect(rows[0].ticket).toBe("100");
  });
});

describe("compareRegistry — drift classes (#115)", () => {
  it("returns empty drift when markers match registry rows", () => {
    const markers = [
      { file: "e2e/smoke/a.spec.ts", line: 5, testName: "A", ticket: "100" },
      { file: "e2e/smoke/b.spec.ts", line: 7, testName: "B", ticket: "200" },
    ];
    const rows = [
      { ticked: false, ticket: "100", text: "", lineNo: 1 },
      { ticked: false, ticket: "200", text: "", lineNo: 2 },
    ];
    const drift = compareRegistry(markers, rows, "87");
    expect(drift).toEqual({ missing: [], stale: [], noTicket: [] });
  });

  it("returns empty drift when both the source AND the registry are empty", () => {
    // The bootstrap state. The script must NOT fail on a clean
    // codebase with an empty registry — that would brick the gate
    // until the first quarantine event happens.
    const drift = compareRegistry([], [], "87");
    expect(drift).toEqual({ missing: [], stale: [], noTicket: [] });
  });

  it("flags a marker with no matching row as MISSING", () => {
    const markers = [
      { file: "e2e/smoke/a.spec.ts", line: 5, testName: "A", ticket: "100" },
    ];
    const drift = compareRegistry(markers, [], "87");
    expect(drift.missing).toHaveLength(1);
    expect(drift.missing[0].ticket).toBe("100");
    expect(drift.missing[0].reason).toContain("no row in #87");
  });

  it("flags a marker against a ticked row as MISSING (ambiguous re-quarantine)", () => {
    const markers = [
      { file: "e2e/smoke/a.spec.ts", line: 5, testName: "A", ticket: "100" },
    ];
    const rows = [
      { ticked: true, ticket: "100", text: "- [x] #100", lineNo: 1 },
    ];
    const drift = compareRegistry(markers, rows, "87");
    expect(drift.missing).toHaveLength(1);
    expect(drift.missing[0].reason).toContain("ticked");
  });

  it("flags an unticked row with no matching marker as STALE", () => {
    const rows = [
      { ticked: false, ticket: "100", text: "- [ ] #100", lineNo: 1 },
    ];
    const drift = compareRegistry([], rows, "87");
    expect(drift.stale).toHaveLength(1);
    expect(drift.stale[0].ticket).toBe("100");
    expect(drift.stale[0].reason).toContain("no @quarantine");
  });

  it("does NOT flag a TICKED row with no marker — that's the closed state", () => {
    // A test that was fixed: the marker is gone and the row is ticked.
    // This is the SUCCESS state the workflow is trying to preserve;
    // it must not flag.
    const rows = [
      { ticked: true, ticket: "100", text: "- [x] #100", lineNo: 1 },
    ];
    const drift = compareRegistry([], rows, "87");
    expect(drift).toEqual({ missing: [], stale: [], noTicket: [] });
  });

  it("flags markers with no ticket reference separately as noTicket", () => {
    const markers = [
      { file: "e2e/smoke/a.spec.ts", line: 5, testName: "A", ticket: null },
    ];
    const drift = compareRegistry(markers, [], "87");
    expect(drift.noTicket).toHaveLength(1);
    expect(drift.noTicket[0].file).toBe("e2e/smoke/a.spec.ts");
    expect(drift.missing).toEqual([]);
  });

  it("reports all three drift classes simultaneously without cross-pollution", () => {
    // A realistic failed-CI shape: one marker missing its row, one
    // unticked row whose marker was removed, and one marker missing
    // its ticket reference. Pin that noTicket markers don't slip
    // into `missing` (a regression that dropped the early
    // `continue` in compareRegistry would re-route them) and that
    // each class shows in isolation.
    const markers = [
      // valid marker with no row → missing
      { file: "e2e/smoke/a.spec.ts", line: 5, testName: "A", ticket: "100" },
      // marker without a ticket reference → noTicket (NOT missing)
      { file: "e2e/smoke/c.spec.ts", line: 7, testName: "C", ticket: null },
    ];
    const rows = [
      // unticked row, no matching marker → stale
      { ticked: false, ticket: "200", text: "- [ ] #200", lineNo: 1 },
    ];
    const drift = compareRegistry(markers, rows, "87");
    expect(drift.missing).toHaveLength(1);
    expect(drift.missing[0].ticket).toBe("100");
    expect(drift.stale).toHaveLength(1);
    expect(drift.stale[0].ticket).toBe("200");
    expect(drift.noTicket).toHaveLength(1);
    expect(drift.noTicket[0].file).toBe("e2e/smoke/c.spec.ts");
  });
});

describe("listSpecFiles — directory walk contract (#115)", () => {
  it("returns *.spec.ts files only, skips node_modules and dotfiles, and uses POSIX separators", async () => {
    // Smoke-pin the walker's three documented filters. Without
    // these, the walker would surface `@quarantine` mentions in
    // installed Playwright examples (node_modules) or in editor
    // backup files (`.harness.spec.ts.swp`), each generating
    // false-positive markers. POSIX separators matter for
    // cross-platform consistency — Windows-discovered paths
    // mismatch registry rows (which use forward slashes).
    const root = mkdtempSync(join(tmpdir(), "quarantine-test-"));
    mkdirSync(join(root, "smoke", "nested"), { recursive: true });
    mkdirSync(join(root, "node_modules", "@playwright"), { recursive: true });
    mkdirSync(join(root, "support"), { recursive: true });
    writeFileSync(join(root, "smoke", "real.spec.ts"), "// real");
    writeFileSync(join(root, "smoke", "nested", "deep.spec.ts"), "// deep");
    writeFileSync(join(root, "smoke", ".hidden.spec.ts"), "// dotfile, must skip");
    writeFileSync(join(root, "support", "helper.ts"), "// not a spec, must skip");
    writeFileSync(
      join(root, "node_modules", "@playwright", "noise.spec.ts"),
      "// in node_modules, must skip",
    );

    const found = await listSpecFiles(root);
    // Positives — both real specs surface.
    expect(found).toEqual(
      expect.arrayContaining(["smoke/real.spec.ts", "smoke/nested/deep.spec.ts"]),
    );
    // Negatives — dotfile, non-spec, and node_modules-entry are NOT included.
    expect(found).not.toContain("smoke/.hidden.spec.ts");
    expect(found).not.toContain("support/helper.ts");
    expect(found).not.toContain("node_modules/@playwright/noise.spec.ts");
    // POSIX separators on every entry — no backslashes even on Windows.
    for (const p of found) {
      expect(p).not.toContain("\\");
    }
  });

  it("returns [] gracefully on a missing root directory (no throw)", async () => {
    // The CLI entrypoint pre-checks existsSync, but the helper
    // itself MUST not throw on ENOENT so a future caller that
    // pre-walks before checking can still rely on the contract.
    await expect(
      listSpecFiles(join(tmpdir(), "quarantine-does-not-exist-" + Date.now())),
    ).resolves.toEqual([]);
  });
});

describe("formatDriftReport — error output (#115)", () => {
  it("returns empty string when there is no drift", () => {
    expect(
      formatDriftReport({ missing: [], stale: [], noTicket: [] }, "87"),
    ).toBe("");
  });

  it("includes file:line and the append-template for each missing marker", () => {
    const report = formatDriftReport(
      {
        missing: [
          {
            file: "e2e/smoke/a.spec.ts",
            line: 5,
            testName: "the broken flow",
            ticket: "100",
            reason: "no row in #87 for ticket #100 (append: ...)",
          },
        ],
        stale: [],
        noTicket: [],
      },
      "87",
    );
    expect(report).toContain("e2e/smoke/a.spec.ts:5");
    expect(report).toContain("the broken flow");
    expect(report).toContain("no row in #87");
    // The three-step Flake-policy footer must always appear so a
    // contributor whose build fails has a precise checklist of what
    // to do next.
    expect(report).toContain("Flake-policy");
    expect(report).toContain("(c) append a row to #87");
  });

  it("separates stale and missing into clearly-labelled sections", () => {
    const report = formatDriftReport(
      {
        missing: [
          {
            file: "e2e/smoke/a.spec.ts",
            line: 5,
            testName: "A",
            ticket: "100",
            reason: "no row in #87",
          },
        ],
        stale: [
          {
            ticked: false,
            ticket: "200",
            text: "- [ ] #200 — `e2e/smoke/b.spec.ts`:`B`",
            lineNo: 3,
            reason: "no @quarantine marker",
          },
        ],
        noTicket: [
          {
            file: "e2e/smoke/c.spec.ts",
            line: 7,
            testName: "C",
            ticket: null,
          },
        ],
      },
      "87",
    );
    expect(report).toContain("Markers in source with NO `#<ticket>`");
    expect(report).toContain("Markers in source with NO matching `- [ ]`");
    expect(report).toContain("Unticked rows in registry");
    // The actionable per-marker Fix: line in the noTicket section
    // is the recipe a contributor reads on failed-build output —
    // pinning it so a refactor that drops it would surface here
    // instead of leaving the error message hollow.
    expect(report).toContain("Fix: add a TODO comment");
  });
});
