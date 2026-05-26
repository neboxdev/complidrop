#!/usr/bin/env node
/**
 * Secret-scan gate for E2E artifacts (#38 AC #5).
 *
 * Walks a directory (default: `frontend/test-results/`) and fails the
 * process (non-zero exit) if any file contains a value matching the
 * pattern table below.
 *
 * Patterns target the textual SHAPE of a secret, not its literal
 * content. They will false-positive on a test fixture that legitimately
 * contains a SSN-shaped string — add the file to `ALLOWLISTED_FILES`
 * with a one-line comment.
 *
 * ZIP support: Playwright's primary diagnostic artifact is `trace.zip`,
 * which is a deflate-compressed archive. A naive `readFile(file, 'utf8')`
 * cannot see inside compressed entries — the leak vectors that matter
 * (network bodies, request headers in `*.network`) are inside those
 * entries. The scanner unzips every `.zip` it finds with `adm-zip` and
 * recursively applies the pattern table to each entry's text content
 * (binary entries like PNG screenshots are skipped — leak text never
 * survives PNG encoding intact).
 *
 * Local safety net: `frontend/package.json` declares `posttest:e2e`
 * which runs this script after every `npm run test:e2e`, so a dev
 * sees a leak before pushing.
 */
import { readFile, readdir, stat } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join, relative, sep, extname } from "node:path";
import AdmZip from "adm-zip";

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
 * Vendor-portal tokens travel via URL PATH (`/api/portal/{token}/upload`)
 * not via a header named `portal-token`. The `portal-token:` partition-
 * key string in the rate-limit code (Program.cs) is server-internal and
 * never crosses the wire. The pattern below catches the URL form.
 */
const PATTERNS = [
  { name: "cd_session cookie value", re: /cd_session=/i },
  { name: "cd_refresh cookie value", re: /cd_refresh=/i },
  // Any value-bearing Authorization header — Bearer, Basic, Token, or
  // bare JWT. The project doesn't use Authorization anywhere in
  // production, so the pattern is broad on purpose.
  { name: "Authorization header", re: /\bauthorization:\s*\S/i },
  // Vendor-portal token in URL path. Tokens are opaque ids ≥16 chars
  // of [A-Za-z0-9_-]; this min-length filter excludes the routes
  // themselves (`/api/portal/...` with no token following) and the
  // partition-key prefix `portal-token:`.
  { name: "vendor portal token in URL path", re: /\/api\/portal\/[A-Za-z0-9_-]{16,}/ },
  // SSN shape: 3-2-4 digits separated by hyphens.
  { name: "SSN-shaped pattern", re: /\b\d{3}-\d{2}-\d{4}\b/ },
  // EIN shape: 2-7 digits with one hyphen between.
  { name: "EIN-shaped pattern", re: /\b\d{2}-\d{7}\b/ },
];

/**
 * Files where a SHAPE match is acceptable for a documented reason.
 * Entries are POSIX-shaped paths (forward slashes), normalized on
 * comparison so Windows runs compare cleanly with Linux CI runs.
 * Today the list is empty; if a future test legitimately renders a
 * SSN/EIN for a redaction assertion, add the path here with a
 * one-line comment.
 */
const ALLOWLISTED_FILES = /** @type {ReadonlyArray<string>} */ ([]);

// Binary file extensions where a UTF-8 decode produces garbage. We skip
// the regex pass on these but explicitly warn (silent skips are a
// stealth-leak vector — a real cookie inside a PDF would slip past
// without any audit trail).
const BINARY_EXTENSIONS = new Set([
  ".png",
  ".jpg",
  ".jpeg",
  ".gif",
  ".webp",
  ".pdf",
  ".woff",
  ".woff2",
  ".ttf",
  ".otf",
]);

/**
 * Walk a directory tree depth-first, yielding every regular-file
 * relative path. Skips dotfiles and symlinks (loop safety).
 */
async function* walk(dir) {
  const entries = await readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.name.startsWith(".")) continue;
    if (entry.isSymbolicLink()) continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      yield* walk(full);
    } else if (entry.isFile()) {
      yield full;
    }
  }
}

const findings = [];
const warnings = [];

for await (const file of walk(root)) {
  const rel = posixRelative(root, file);
  if (ALLOWLISTED_FILES.includes(rel)) continue;
  const info = await stat(file);
  // Skip files >100 MB with a loud warning. Playwright trace.zip is
  // typically <10 MB; an artifact this large is anomalous and should
  // be manually reviewed.
  if (info.size > 100 * 1024 * 1024) {
    warnings.push(`${rel}: skipped (size ${info.size} > 100 MB) — manual review required`);
    continue;
  }
  await scanOne(file, rel);
}

if (warnings.length > 0) {
  for (const w of warnings) console.warn(`scan-secrets: WARN ${w}`);
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

console.log(
  `scan-secrets: clean (${root} contains no patterns matching the secret table).`,
);
process.exit(0);

/** Scan one file. Recurses into .zip via adm-zip. */
async function scanOne(file, rel) {
  const ext = extname(file).toLowerCase();
  if (ext === ".zip") {
    await scanZip(file, rel);
    return;
  }
  if (BINARY_EXTENSIONS.has(ext)) {
    // Binary file types where UTF-8 decode is garbage. Skip with a
    // warning so the audit trail is explicit; a leak hiding inside a
    // PDF/PNG would be a different kind of bug worth its own ticket.
    return;
  }
  let text;
  try {
    text = await readFile(file, "utf8");
  } catch (err) {
    warnings.push(`${rel}: unreadable as utf8 (${err?.code ?? "unknown"}) — skipped`);
    return;
  }
  scanText(text, rel);
}

/**
 * Unzip in-memory (adm-zip's sync API is fine — trace files are <10 MB
 * typical) and scan each entry. Recurses into nested .zip in case a
 * future Playwright nests trace files; harmless if not.
 */
async function scanZip(file, rel) {
  let zip;
  try {
    zip = new AdmZip(file);
  } catch (err) {
    warnings.push(`${rel}: not a valid zip (${err?.message ?? "unknown"}) — skipped`);
    return;
  }
  for (const entry of zip.getEntries()) {
    if (entry.isDirectory) continue;
    const entryRel = `${rel}::${entry.entryName}`;
    if (ALLOWLISTED_FILES.includes(entryRel)) continue;
    const innerExt = extname(entry.entryName).toLowerCase();
    if (BINARY_EXTENSIONS.has(innerExt)) continue;
    if (innerExt === ".zip") {
      // Rare: a nested zip inside trace.zip. adm-zip can be re-instantiated
      // on a Buffer.
      try {
        const inner = new AdmZip(entry.getData());
        for (const innerEntry of inner.getEntries()) {
          if (innerEntry.isDirectory) continue;
          const innerInnerExt = extname(innerEntry.entryName).toLowerCase();
          if (BINARY_EXTENSIONS.has(innerInnerExt)) continue;
          const txt = innerEntry.getData().toString("utf8");
          scanText(txt, `${entryRel}::${innerEntry.entryName}`);
        }
      } catch (err) {
        warnings.push(`${entryRel}: nested zip unreadable (${err?.message ?? "unknown"})`);
      }
      continue;
    }
    let txt;
    try {
      txt = entry.getData().toString("utf8");
    } catch (err) {
      warnings.push(`${entryRel}: entry unreadable as utf8 (${err?.message ?? "unknown"}) — skipped`);
      continue;
    }
    scanText(txt, entryRel);
  }
}

/** Run the pattern table against one text blob. */
function scanText(text, label) {
  for (const { name, re } of PATTERNS) {
    const match = text.match(re);
    if (match) {
      findings.push({ file: label, pattern: name, snippet: snippet(text, match.index ?? 0) });
    }
  }
}

function snippet(text, index) {
  const start = Math.max(0, index - 30);
  const end = Math.min(text.length, index + 40);
  return text.slice(start, end).replace(/\s+/g, " ").slice(0, 70);
}

/** POSIX-style relative path so allowlist + finding output match cross-platform. */
function posixRelative(from, to) {
  return relative(from, to).split(sep).join("/");
}
