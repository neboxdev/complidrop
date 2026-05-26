#!/usr/bin/env node
/**
 * E2E import guard (#38 AC #6).
 *
 * The AC requires explicit "confirmation that ExtractionFixtures are
 * synthetic and are NOT imported here." Convention via README is not
 * sufficient — a future contributor adding
 * `import { ... } from "../../../api/CompliDrop.Api.Tests/ExtractionFixtures/..."`
 * would silently violate the AC.
 *
 * This script walks `frontend/e2e/` and fails (non-zero exit) on:
 *   1. Any reference to `ExtractionFixtures` (case-insensitive).
 *   2. Any import path that reaches across the project root into
 *      `api/` (backend test code).
 *
 * Run locally and in CI as a fast lint step. Failure mode is loud and
 * unambiguous, with the offending file and the matched line.
 */
import { readFile, readdir } from "node:fs/promises";
import { join, relative, sep, extname } from "node:path";

const ROOT = "e2e";

const FORBIDDEN_PATTERNS = [
  {
    name: "ExtractionFixtures import",
    // Only flag actual import / require statements referencing
    // ExtractionFixtures. A docstring mention ('NOT imported here')
    // is fine and educational — only code-level imports violate AC #6.
    re: /(import\s+[^;]*?ExtractionFixtures|from\s+['"][^'"]*ExtractionFixtures|require\(['"][^'"]*ExtractionFixtures)/,
    note: "Backend ExtractionFixtures must NOT be imported by E2E (AC #6).",
  },
  {
    name: "cross-tree backend import",
    // Matches an `import ... from "...api/CompliDrop.Api..."` or any
    // dynamic `require("...api/CompliDrop.Api...")`. Restricted to
    // module-specifier strings so comments mentioning the path are
    // allowed.
    re: /(from\s+['"][^'"]*api\/CompliDrop\.Api|require\(['"][^'"]*api\/CompliDrop\.Api)/,
    note: "E2E tests must not import backend code; mock at the page.route boundary.",
  },
];

async function* walk(dir) {
  const entries = await readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.name.startsWith(".")) continue;
    if (entry.isSymbolicLink()) continue;
    if (entry.isDirectory() && entry.name === "node_modules") continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      yield* walk(full);
    } else if (entry.isFile()) {
      yield full;
    }
  }
}

const findings = [];
const exts = new Set([".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"]);

for await (const file of walk(ROOT)) {
  // Skip the lint script itself (the script literally references
  // "ExtractionFixtures" in its own pattern table).
  const rel = relative(ROOT, file).split(sep).join("/");
  if (rel === "scripts/lint-imports.mjs") continue;
  if (!exts.has(extname(file).toLowerCase())) continue;
  const text = await readFile(file, "utf8");
  const lines = text.split(/\r?\n/);
  for (const { name, re, note } of FORBIDDEN_PATTERNS) {
    lines.forEach((line, i) => {
      if (re.test(line)) {
        findings.push({ file: rel, line: i + 1, name, note, snippet: line.trim() });
      }
    });
  }
}

if (findings.length > 0) {
  console.error("\nlint-imports: FAIL — forbidden patterns in frontend/e2e/:\n");
  for (const f of findings) {
    console.error(`  ${f.file}:${f.line}`);
    console.error(`    ${f.name} — ${f.note}`);
    console.error(`    > ${f.snippet}`);
    console.error("");
  }
  process.exit(1);
}

console.log("lint-imports: clean (no forbidden imports in frontend/e2e/).");
process.exit(0);
