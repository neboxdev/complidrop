#!/usr/bin/env node
/**
 * Secret-scan gate for E2E artifacts (#38 AC).
 *
 * Walks a directory (default: `frontend/test-results/`) and fails the
 * process (non-zero exit) if any file contains a value matching:
 *
 *   - `cd_session=` or `cd_refresh=` (the project's httpOnly JWT
 *     cookie names from CLAUDE.md — they must NEVER appear in a
 *     committed or uploaded artifact)
 *   - `Authorization:` header followed by a Bearer / Basic token
 *     (case-insensitive; tests should NOT include either, but CI
 *     guards against accidental capture)
 *   - `portal-token` as a header name (rate-limit key) followed by a
 *     value (mirroring the cookie concern for vendor-portal tokens)
 *   - SSN-shaped patterns `\d{3}-\d{2}-\d{4}` (e.g. 123-45-6789)
 *   - EIN-shaped patterns `\d{2}-\d{7}` (e.g. 12-3456789)
 *
 * Trace.zip from Playwright is treated as a text file via best-effort
 * UTF-8 read; the trace internals are JSON + base64-encoded blobs, so
 * the regex hits ride on the JSON portion. If a future Playwright
 * release moves to a binary format, replace the file walker with an
 * unzip step.
 *
 * The script is also invoked by the `pretest:e2e` npm script (so a
 * dev catches a leak locally before pushing), AND by frontend-ci.yml
 * AFTER the Playwright run but BEFORE artifact upload.
 *
 * Allowlist tokens used in tests: any synthetic token whose VALUE
 * (not name) matches the patterns is fine, but the cookie/header
 * NAMES `cd_session=` / `cd_refresh=` / `Authorization:` are flagged
 * regardless of value because their presence implies the test is
 * leaking real auth credentials.
 */
import { readFile, readdir, stat } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join, relative } from "node:path";

// Default scan target = frontend/test-results/. Override via argv[2].
const root = process.argv[2] ?? "test-results";

if (!existsSync(root)) {
  console.log(`scan-secrets: no ${root} directory — nothing to scan, exiting clean.`);
  process.exit(0);
}

/**
 * Pattern table. Each entry has a name (for the failure report) and a
 * regex. Order doesn't matter — every pattern runs on every file.
 *
 * NOTE: patterns target the textual SHAPE of a secret, not its
 * literal content. They will false-positive on a test fixture that
 * legitimately CONTAINS a 123-45-6789-shaped string for a different
 * reason — in that case, add the file to ALLOWLISTED_FILES below
 * with a one-line justification, NOT a regex carve-out (carve-outs
 * decay; allowlists stay explicit).
 */
const PATTERNS = [
  { name: "cd_session cookie value", re: /cd_session=/i },
  { name: "cd_refresh cookie value", re: /cd_refresh=/i },
  { name: "Authorization header", re: /\bauthorization:\s*(bearer|basic)\b/i },
  { name: "portal-token header value", re: /\bportal-token:\s*\S/i },
  // SSN shape: 3-2-4 digits separated by hyphens. Surrounded by
  // word boundaries so we don't match a phone number with extra
  // digits or a UUID fragment.
  { name: "SSN-shaped pattern", re: /\b\d{3}-\d{2}-\d{4}\b/ },
  // EIN shape: 2-7 digits with one hyphen between.
  { name: "EIN-shaped pattern", re: /\b\d{2}-\d{7}\b/ },
];

// Files where a SHAPE match is acceptable for a documented reason.
// Today the list is empty; if a future test needs a literal SSN in a
// fixture (e.g. asserting redaction logic), add the path here with a
// one-line comment.
const ALLOWLISTED_FILES = [];

/**
 * Walk a directory tree depth-first, yielding every regular-file
 * relative path. Skips dotfiles to keep the scan output tractable.
 */
async function* walk(dir) {
  const entries = await readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.name.startsWith(".")) continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      yield* walk(full);
    } else if (entry.isFile()) {
      yield full;
    }
  }
}

const findings = [];
for await (const file of walk(root)) {
  const rel = relative(root, file);
  if (ALLOWLISTED_FILES.includes(rel)) continue;
  // Skip files > 50 MB — Playwright trace.zip is typically <10 MB,
  // but a video artifact (we set videos OFF) or a screenshot bundle
  // would slip past this cap.
  const info = await stat(file);
  if (info.size > 50 * 1024 * 1024) continue;
  let text;
  try {
    text = await readFile(file, "utf8");
  } catch {
    // Binary file or unreadable — skip. Playwright's trace.zip reads
    // as UTF-8 because the manifest + JSON portions are textual; if a
    // file is truly binary the regex would not catch anything useful.
    continue;
  }
  for (const { name, re } of PATTERNS) {
    const match = text.match(re);
    if (match) {
      findings.push({ file: rel, pattern: name, snippet: snippet(text, match.index ?? 0) });
    }
  }
}

if (findings.length > 0) {
  console.error("\nscan-secrets: FAIL — sensitive patterns found in artifacts:\n");
  for (const f of findings) {
    console.error(`  ${f.file}`);
    console.error(`    pattern: ${f.pattern}`);
    console.error(`    near: ${f.snippet}`);
    console.error("");
  }
  console.error(
    "If a finding is a false-positive (e.g. a fixture SSN that's intentional),",
  );
  console.error(
    "add the file to ALLOWLISTED_FILES in frontend/e2e/scripts/scan-secrets.mjs",
  );
  console.error("with a one-line comment.");
  process.exit(1);
}

console.log(`scan-secrets: clean (${root} contains no patterns matching the secret table).`);
process.exit(0);

function snippet(text, index) {
  const start = Math.max(0, index - 30);
  const end = Math.min(text.length, index + 40);
  // Collapse whitespace + replace newlines so the output is one line
  // per finding.
  return text
    .slice(start, end)
    .replace(/\s+/g, " ")
    .slice(0, 70);
}
