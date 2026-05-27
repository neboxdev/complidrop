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

const repoFrontendSrc = resolve(__dirname, "..");
const apiPath = resolve(__dirname, "api.ts");

describe("api.ts — 'use client' directive + client-only guard (#120)", () => {
  it("'use client' is the literal first non-blank source line", () => {
    // Strip a UTF-8 BOM if present (some Windows editors prepend one)
    // so the test isn't tripped by encoding artifacts. The `﻿`
    // escape form is preferred over a literal BOM glyph because the
    // escape survives any source-file transform (Prettier reformat,
    // editor encoding re-save) that might invisibly normalize a
    // literal BOM character.
    const source = readFileSync(apiPath, "utf8").replace(/^﻿/, "");
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
  // imports from `@/lib/api` (or a relative-path variant), and
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
        // Skip vendored / build directories. These directories
        // shouldn't exist under src/ in practice, but defending
        // against an accidental commit is cheap.
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

  // `@/lib/api` and explicit relative variants (../lib/api, ../../
  // lib/api, etc.). The relative forms cover the case where a future
  // file outside `src/app` or `src/hooks` imports the api by
  // relative path.
  const importMatcher =
    /from\s+["'](?:@\/lib\/api|(?:\.\.\/)+lib\/api)["']/;

  const importers = allFiles.filter((file) => {
    // Exclude tests so we don't recurse on this very file or
    // re-check api.test.ts (both call `from "@/lib/api"` but are
    // themselves vitest tests, not client modules).
    if (file.endsWith(".test.ts") || file.endsWith(".test.tsx")) {
      return false;
    }
    // Exclude api.ts itself.
    if (file === apiPath) return false;
    const source = readFileSync(file, "utf8");
    return importMatcher.test(source);
  });

  it("the importer audit picks up the known set (regression guard against the walk going dark)", () => {
    // Sanity-check the walk: a refactor that broke the path
    // resolution or the import regex would cause `importers` to be
    // empty, which would make every subsequent assertion vacuously
    // pass. Pin a floor on the importer count so the test fails
    // loudly if the walk goes dark. The floor is generous (today's
    // count is 14 client modules, audited at #120 time); a count
    // below 10 means something is structurally wrong with the walk.
    expect(importers.length).toBeGreaterThanOrEqual(10);
  });

  it.each(
    // Use `it.each` so a failure surfaces ONE bad importer at a time
    // rather than collapsing every importer into a single failed
    // expectation. Each file gets its own test case named with its
    // path relative to frontend/src so the diagnostic message
    // points directly at the offending file. Static fallback
    // tuple keeps `it.each` happy even on the unreachable empty
    // case (the floor assertion above would have already failed).
    importers.length > 0
      ? importers.map((file) => [relative(repoFrontendSrc, file), file] as const)
      : [["<no importers found — see floor assertion above>", apiPath] as const],
  )(
    "importer %s starts with the 'use client' directive",
    (_relPath, file) => {
      const source = readFileSync(file, "utf8").replace(/^﻿/, "");
      const firstSourceLine = source
        .split(/\r?\n/)
        .find((line) => line.trim().length > 0);
      expect(firstSourceLine).toMatch(useClientDirective);
    },
  );
});
