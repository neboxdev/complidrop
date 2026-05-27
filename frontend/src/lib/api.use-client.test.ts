/**
 * Pins the client-boundary protections at the top of `api.ts` (#120).
 *
 * Two layers guard the refresh-coalescing singleton against accidental
 * SSR cross-request state-sharing:
 *
 *   1. The `"use client"` directive on the first line — Next.js
 *      treats the module as a client boundary, so its top-level code
 *      runs in the client bundle once imported.
 *   2. `import "client-only"` immediately after — provides a HARD
 *      build-time error if a Server Component / Route Handler /
 *      middleware ever imports the module transitively. `client-only`
 *      re-exports a stub that throws when bundled server-side.
 *
 * Why test these structural invariants instead of trusting the file
 * contents? Because they are protections only as long as they remain
 * in place:
 *   - A careless refactor (prettier reformat, import sorter, comment
 *     sweep) could drop or shuffle the directive without TypeScript /
 *     ESLint flagging it.
 *   - A future contributor adding a new caller from a Server
 *     Component would (with `client-only`) get a clear build error,
 *     but only if the import stays. Without an automated importer
 *     audit, a careless removal silently re-opens the SSR
 *     contamination risk.
 *   - The protections are structural — once removed, every existing
 *     runtime test still passes while the latent risk reopens.
 *
 * This file IS the regression pin for all three properties:
 *   - directive present and first
 *   - directive appears exactly once
 *   - `client-only` import present
 * Plus the AC #3 invariant from the ticket: every importer of
 * `@/lib/api` (the existing 14 audited at #120 time, and every
 * future addition) must itself be `"use client"`.
 *
 * Read the api.ts source via `fs.readFileSync` rather than importing
 * it, because directives are erased after parsing — they're only
 * observable from raw source.
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { resolve, join, relative } from "node:path";
import { describe, it, expect } from "vitest";

const useClientDirective = /^["']use client["']\s*;?\s*$/;

// Test-file detector — mirrors the vitest config's
// `include: ["src/**/*.{test,spec}.{ts,tsx}"]` glob so a future
// `.spec.tsx` test wouldn't spuriously fail the importer audit.
// (#120 second-pass review — correctness reviewer.)
const TEST_FILE_RE = /\.(test|spec)\.tsx?$/;

// BOM strip — uses the explicit `\uFEFF` escape (NOT a literal
// U+FEFF byte) so the regex source itself survives any aggressive
// encoding transform (Notepad++ "Encode in UTF-8 (no BOM)",
// dos2unix --keep-bom no, certain git filter drivers). If an
// editor stripped a literal BOM from this source file, the regex
// would silently become a no-op /^/ that always matches at
// position 0 — and the BOM-strip defense would be gone with no
// failing test. (#120 second-pass review — correctness reviewer.)
const BOM_RE = /^\uFEFF/;

const repoFrontendSrc = resolve(__dirname, "..");
const apiPath = resolve(__dirname, "api.ts");

describe("api.ts — 'use client' directive + client-only guard (#120)", () => {
  it("'use client' is the literal first non-blank source line", () => {
    const source = readFileSync(apiPath, "utf8").replace(BOM_RE, "");
    // Split on either LF or CRLF — Windows checkouts via
    // `core.autocrlf=true` land CRLF on disk while macOS/Linux land
    // LF, so the test must tolerate both regardless of the
    // contributor's git config.
    const firstSourceLine = source
      .split(/\r?\n/)
      .find((line) => line.trim().length > 0);

    expect(firstSourceLine).toMatch(useClientDirective);
  });

  it("the 'use client' directive appears exactly once (no duplicate-on-merge)", () => {
    // `findIndex` (used in Test 1) only sees the FIRST match — a
    // duplicate directive introduced by a merge conflict resolution
    // would slip past every other assertion in this file. Pin
    // uniqueness here so the duplicate-paste regression is caught
    // structurally. Next.js tolerates duplicates today but the
    // unused string literal would be dead code on every consumer.
    const source = readFileSync(apiPath, "utf8");
    const directiveLines = source
      .split(/\r?\n/)
      .filter((line) => useClientDirective.test(line.trim()));
    expect(directiveLines).toHaveLength(1);
  });

  it("imports the 'client-only' package as a hard build-time SSR guard", () => {
    // `client-only` (shipped transitively via `next`) re-exports a
    // stub that throws when bundled in a server context. Pairing it
    // with the `"use client"` directive upgrades the protection from
    // soft (the module simply runs on the client when imported) to
    // hard (a Server Component import surfaces as a build break).
    // Pin the import so a future refactor that drops it leaves only
    // the soft layer in place and loses the build-time fail-loud.
    const source = readFileSync(apiPath, "utf8");
    // Tolerate single- or double-quoted form, with or without
    // trailing semicolon — the bundler accepts any.
    expect(source).toMatch(/^\s*import\s+["']client-only["']\s*;?\s*$/m);
  });
});

describe("api.ts — every importer must itself be 'use client' (#120 AC #3)", () => {
  // Walk the entire `frontend/src/` tree, find every file that
  // imports from `@/lib/api` (or any relative-path variant), and
  // assert each one starts with the `"use client"` directive. This
  // turns the ticket's human-time-bound audit into an automated
  // invariant: when a future PR adds a new Server Component importer
  // of api.ts, this test fails with the offending file path before
  // the latent SSR-contamination risk can reach production.
  //
  // Test files are excluded because (a) vitest tests run in node
  // and don't need the directive to function, and (b) test files
  // never end up in the Next.js client bundle.
  function walk(dir: string, out: string[] = []): string[] {
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry);
      const st = statSync(full);
      if (st.isDirectory()) {
        // Skip vendored / build directories. These shouldn't exist
        // under src/ in practice, but defending against an accidental
        // commit is cheap.
        if (entry === "node_modules" || entry === ".next") continue;
        walk(full, out);
      } else if (
        st.isFile() &&
        (full.endsWith(".ts") || full.endsWith(".tsx"))
      ) {
        out.push(full);
      }
    }
    return out;
  }

  const allFiles = walk(repoFrontendSrc);

  // Broad matcher catches every import shape that pulls in api.ts:
  //   - `from "@/lib/api"`              alias from anywhere in src/
  //   - `from "../lib/api"` (any depth) relative
  //   - `from "./api"`                  sibling within src/lib/
  //   - `from "@/lib/api.ts"`           with explicit extension
  //   - `import "@/lib/api"`            side-effect import (no `from`)
  //   - `await import("@/lib/api")`     dynamic import
  // `import type` is NOT matched because type-only imports are
  // erased by the TypeScript compiler before any module-level
  // code runs — they're safe to use from Server Components.
  // (#120 second-pass review — correctness reviewer.)
  const importMatcher =
    /(?:from\s+|import\s*\(\s*|import\s+)["'](?:@\/lib\/api(?:\.tsx?)?|(?:\.\.\/)+lib\/api(?:\.tsx?)?|\.\/api(?:\.tsx?)?)["']/;

  const importers = allFiles.filter((file) => {
    if (TEST_FILE_RE.test(file)) return false;
    // Exclude api.ts itself — it's the module being audited, not
    // an importer of itself.
    if (file === apiPath) return false;
    const source = readFileSync(file, "utf8");
    // For `./api` we need the file to actually live inside
    // src/lib/ — a `./api` import from elsewhere would resolve to
    // a different module, not api.ts. The regex above accepts
    // `./api` from anywhere; this guard tightens it.
    if (importMatcher.test(source)) {
      // If the only match is the `./api` form, require the file
      // to be in src/lib/ for the match to count. Otherwise the
      // sibling-relative form is a false positive (unlikely but
      // possible if a future module is also named `api.ts`).
      const usesSiblingForm = /(?:from\s+|import\s*\(\s*|import\s+)["']\.\/api(?:\.tsx?)?["']/.test(
        source,
      );
      const usesAliasOrParentForm =
        /(?:from\s+|import\s*\(\s*|import\s+)["'](?:@\/lib\/api|(?:\.\.\/)+lib\/api)(?:\.tsx?)?["']/.test(
          source,
        );
      if (usesAliasOrParentForm) return true;
      if (usesSiblingForm) {
        // Allow only if the file lives inside src/lib/.
        const dir = relative(repoFrontendSrc, file);
        return dir.split(/[\\/]/)[0] === "lib";
      }
      return false;
    }
    return false;
  });

  it("the importer audit walk found files AND picked up the known set (regression guard against silent dark walks)", () => {
    // Two independent invariants pinned together with a custom
    // diagnostic message so a future failure points the reader at
    // the correct resolution:
    //   1. The walk read SOMETHING — `allFiles` is non-trivial.
    //   2. The importer regex matched a credible count (today's
    //      audit found 14 client modules; pinning a floor of 10
    //      leaves slack for legitimate consolidation while still
    //      catching a regex regression that drops every match).
    // If invariant 1 fails the walk is broken; if invariant 2
    // fails either the regex is broken OR the codebase legitimately
    // shrank and the floor needs updating. The error message
    // guides the resolution. (#120 second-pass review —
    // test-quality reviewer.)
    expect(
      allFiles.length,
      "frontend/src walk found no .ts/.tsx files — either the walk is broken or the path resolution regressed",
    ).toBeGreaterThan(50);
    expect(
      importers.length,
      "importer audit found fewer client modules than the #120 baseline of 14 — either the import-matching regex regressed (check importMatcher), or the codebase legitimately consolidated and the floor of 10 needs updating",
    ).toBeGreaterThanOrEqual(10);
  });

  // Throw at MODULE LOAD if the walk produced zero importers — this
  // is a load-bearing precondition for `it.each` below. Without it,
  // an `it.each([])` would register zero test cases (silently), and
  // the floor assertion above (registered as its own `it`) is the
  // only safety net. If a future refactor disables or renames that
  // assertion, the audit becomes vacuous with no remaining signal.
  // A module-load throw fails the entire test file with a clear
  // diagnostic regardless of which other tests survive. (#120
  // second-pass review — correctness reviewer.)
  if (importers.length === 0) {
    throw new Error(
      "api.use-client.test.ts: importer walk produced 0 results. " +
        "Either the path resolution broke (check resolve(__dirname, '..') → repoFrontendSrc), " +
        "the importMatcher regex regressed, or every importer was inadvertently moved out of src/. " +
        "See the floor assertion in the same describe block for the upstream diagnostic.",
    );
  }

  it.each(
    // One row per importer so a failure surfaces the offending file
    // path in the test name via `%s` — sharper diagnostic than
    // collapsing every importer into a single expectation.
    importers.map(
      (file) => [relative(repoFrontendSrc, file), file] as const,
    ),
  )(
    "importer %s starts with the 'use client' directive",
    (_relPath, file) => {
      const source = readFileSync(file, "utf8").replace(BOM_RE, "");
      const firstSourceLine = source
        .split(/\r?\n/)
        .find((line) => line.trim().length > 0);
      expect(firstSourceLine).toMatch(useClientDirective);
    },
  );
});
