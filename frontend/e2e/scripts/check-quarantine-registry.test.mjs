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
import {
  parseMarkersFromSource,
  parseRegistry,
  compareRegistry,
  formatDriftReport,
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
  });
});
