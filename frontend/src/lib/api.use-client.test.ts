/**
 * Pins the `"use client"` directive at the top of `api.ts` (#120).
 *
 * The directive forces Next.js to fail-loud at build time if a future
 * Server Component, Route Handler, or middleware tries to import the
 * api client. The reasoning is in `api.ts`'s own header comment — short
 * version: the refresh-coalescing singleton's module-level state is
 * per-process under SSR, which would cross-contaminate user sessions.
 *
 * Why test the directive instead of trusting the file's contents?
 * Because:
 *   1. A directive is just a string literal at the top of the file —
 *      a careless refactor (mass `prettier` re-format, an import-sort
 *      lint rule, a "clean up unused comments" sweep) could shuffle or
 *      drop it without TypeScript or ESLint flagging the change.
 *   2. The protection it provides is purely structural — once removed,
 *      every existing test would still pass while the latent SSR
 *      contamination risk silently re-opens.
 *   3. Without an explicit pin, there is no automated regression
 *      surface for "the directive must still be at the top". This test
 *      IS that pin.
 *
 * Read the file via `fs.readFileSync` rather than importing it,
 * because the imported module's runtime exports don't carry the
 * directive — directives are erased after the parsing pass. Reading
 * the source is the only way to observe them from a test.
 */
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, it, expect } from "vitest";

describe("api.ts — 'use client' directive (#120)", () => {
  // Resolve relative to the test file's dirname so the path is
  // independent of vitest's cwd / monorepo nesting. `__dirname`-style
  // resolution via `import.meta.url` would also work but
  // `__filename`/`__dirname` aren't available in ESM and the
  // `import.meta` shape varies across runtimes — `resolve` from the
  // test file's known relative position is the most portable.
  const apiPath = resolve(__dirname, "api.ts");

  it("'use client' is the literal first non-blank source line", () => {
    // Strip a BOM if one is present (Windows editors occasionally
    // prepend one) so the test isn't tripped by encoding artifacts.
    // Then split on either Unix or Windows line endings — the repo's
    // .gitattributes lets either coexist locally — and find the first
    // line with any non-whitespace content.
    const source = readFileSync(apiPath, "utf8").replace(/^﻿/, "");
    const firstSourceLine = source
      .split(/\r?\n/)
      .find((line) => line.trim().length > 0);

    // Accept both single- and double-quoted forms with optional
    // trailing semicolon — the directive prologue is recognized
    // regardless. Anchored so a regression that buries the directive
    // mid-line (e.g. `const x = 1; "use client";`) is also caught.
    expect(firstSourceLine).toMatch(/^["']use client["']\s*;?\s*$/);
  });

  it("the directive appears BEFORE any import, export, or other code statement", () => {
    // The Next.js spec requires the directive to be a "directive
    // prologue" — i.e. it must precede every non-comment statement in
    // the file. Comments above the directive are syntactically
    // allowed by JavaScript but the project's convention (matches
    // every other client module: `useDocuments.ts`, every `page.tsx`)
    // is directive-first, then explanatory comments, then code. Pin
    // that convention so a refactor that moves the directive below a
    // freshly-added `import` is caught at the test layer rather than
    // at production-build time (the latter is where Next.js's own
    // diagnostic surfaces; we want the SHORTER feedback loop).
    const source = readFileSync(apiPath, "utf8");
    const lines = source.split(/\r?\n/);

    // Find the first index of an import/export/code statement,
    // then the first index of the directive. The directive's index
    // must be smaller.
    const isCodeLine = (line: string) => {
      const trimmed = line.trim();
      // Skip blank lines and line comments. Block comments could span
      // multiple lines; rather than build a full comment-state
      // machine, accept lines that start with `*` (continuation of
      // a `/** ... */` block) as comment-continuations. This is
      // sufficient for the convention the file uses.
      if (trimmed.length === 0) return false;
      if (trimmed.startsWith("//")) return false;
      if (trimmed.startsWith("/*")) return false;
      if (trimmed.startsWith("*")) return false;
      // Treat the directive itself as not-a-code-statement so it
      // doesn't satisfy the predicate before its own index is found.
      if (/^["']use client["']\s*;?\s*$/.test(trimmed)) return false;
      return true;
    };

    const directiveIndex = lines.findIndex((line) =>
      /^["']use client["']\s*;?\s*$/.test(line.trim()),
    );
    const firstCodeIndex = lines.findIndex(isCodeLine);

    expect(directiveIndex).toBeGreaterThanOrEqual(0);
    expect(firstCodeIndex).toBeGreaterThanOrEqual(0);
    // Strict less-than: directive must come BEFORE the first code
    // line. Equal would mean the same line, which can't happen
    // because the directive-recognizer filter would have rejected
    // that line in `isCodeLine`.
    expect(directiveIndex).toBeLessThan(firstCodeIndex);
  });
});
