/**
 * Contract for the shared counsel-register reader, plus the CI trigger it
 * depends on.
 *
 * `G1-COUNSEL-BRIEF.md` lives OUTSIDE `frontend/`, so nothing about editing it
 * would run the frontend suite by default — `frontend-ci` names it explicitly
 * as a path trigger, exactly like the shared contact-email corpus. That trigger
 * carries a comment saying WHY, and the comment named only the CLM-4 pin after
 * a second consumer (CLM-5, #404) had already landed. A stale "why" is how a
 * trigger gets deleted as unused, so the naming is pinned here rather than
 * trusted: any test that reads the brief must be named in the workflow.
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, it, expect } from "vitest";
import { COUNSEL_BRIEF_PATH, counselRegisterRow, flattenCopy } from "./counsel-brief";

const FRONTEND_SRC = resolve(__dirname, "..");
const WORKFLOW = resolve(__dirname, "..", "..", "..", ".github", "workflows", "frontend-ci.yml");
const TEST_FILE_RE = /\.(test|spec)\.tsx?$/;

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      if (entry === "node_modules" || entry === ".next") continue;
      walk(full, out);
    } else if (st.isFile() && TEST_FILE_RE.test(full)) {
      out.push(full);
    }
  }
  return out;
}

/**
 * Comments are stripped before the scan: several suites MENTION the brief in
 * their header prose without reading a byte of it (`marketing-content.test.tsx`
 * explains why its assertions exist), and naming those in the CI comment would
 * make the trigger's stated reason wrong in the other direction.
 */
function stripComments(source: string): string {
  return source.replace(/\/\*[\s\S]*?\*\//g, " ").replace(/(^|[^:])\/\/[^\n]*/g, "$1");
}

/** Suites that actually READ the brief — through this module, or by path. */
function suitesReadingTheBrief(): string[] {
  return walk(FRONTEND_SRC)
    .filter((file) => {
      const code = stripComments(readFileSync(file, "utf8"));
      return (
        /from\s+["'][^"']*counsel-brief["']/.test(code) || code.includes("G1-COUNSEL-BRIEF.md")
      );
    })
    .map((file) => `src/${relative(FRONTEND_SRC, file).replace(/\\/g, "/")}`)
    .filter((path) => path !== "src/test/counsel-brief.test.ts"); // this file
}

describe("counsel-register reader (#404 review S6)", () => {
  it("returns exactly one row per §0 register item, with its quoted copy", () => {
    for (const item of ["CLM-4", "CLM-5"]) {
      const { rows, quoted } = counselRegisterRow(item);
      expect(rows, `expected exactly one §0 ${item} register row`).toHaveLength(1);
      expect(quoted.length, `${item} quotes no copy`).toBeGreaterThan(0);
      // The quotes come from the row it returned, not from anywhere else in the file.
      for (const sentence of quoted) expect(rows[0]).toContain(sentence);
    }
  });

  it("matches §0's BOLD id form only, so §C's detail row can never be returned instead", () => {
    const brief = readFileSync(COUNSEL_BRIEF_PATH, "utf8");
    // §C writes the same ids unbolded; if the filter ever loosened, CLM-5 would
    // match two rows and every quote assertion would silently go empty.
    expect(brief).toMatch(/^\| CLM-5 \|/m);
    expect(counselRegisterRow("CLM-5").rows).toHaveLength(1);
  });

  it("reports no rows (rather than throwing) for an item that does not exist yet", () => {
    const { rows, quoted } = counselRegisterRow("CLM-99");
    expect(rows).toEqual([]);
    expect(quoted).toEqual([]);
  });

  it("flattens a quote wrapped across lines into one comparable phrase", () => {
    expect(flattenCopy("  By uploading,\n  you agree  ")).toBe("By uploading, you agree");
  });
});

describe("frontend-ci names every suite that reads the counsel brief (#404 review S7)", () => {
  it("finds the suites at all", () => {
    // Guards the guard: a walk that stopped reaching them would make the
    // assertion below pass against an empty set.
    const suites = suitesReadingTheBrief();
    expect(suites).toContain("src/test/marketing-claims.test.ts");
    expect(suites).toContain("src/app/portal/[token]/page.test.tsx");
  });

  it("names each of them beside the path trigger", () => {
    const workflow = readFileSync(WORKFLOW, "utf8");
    expect(workflow).toContain('- "docs/rule-engine/G1-COUNSEL-BRIEF.md"');
    for (const suite of suitesReadingTheBrief()) {
      expect(
        workflow,
        `frontend-ci triggers on the counsel brief but its comment does not name ${suite}, ` +
          "which reads it — a trigger whose stated reason is incomplete is one a future " +
          "contributor prunes as over-broad",
      ).toContain(suite);
    }
  });
});
