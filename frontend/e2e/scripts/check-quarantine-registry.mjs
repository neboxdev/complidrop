#!/usr/bin/env node
/**
 * Quarantine-registry drift check (#115).
 *
 * Mechanically enforces ADR 0010 §Flake policy + #87's Quarantine
 * registry: every `@quarantine` / `test.fixme(` marker in
 * `frontend/e2e/**\/*.spec.ts` must have a matching `- [ ]` row in
 * issue #87's body, and every unticked row in #87 must point at a
 * ticket whose marker is still in the source tree.
 *
 * The Flake-policy procedure has three discipline steps: (a) tag the
 * test with `@quarantine`/`test.fixme`, (b) file a `bug`-labelled
 * ticket, (c) append a row to #87. Only (b) is mechanically guaranteed
 * today (via `bugfix-epic-sync.yml`). This script closes (a) ↔ (c):
 * either a marker without a row, OR an unticked row without a marker,
 * fails the build with a clear, file:line-anchored message naming
 * which entry is missing.
 *
 * Sibling shape:
 *   - `lint-imports.mjs` — codebase scan that fails on convention
 *     drift. Same Node + `process.exit(1)` shape.
 *   - `bugfix-epic-sync.yml` — managed-block sync via `gh`. This
 *     script READS the issue body via `gh` but does NOT write.
 *
 * Configuration:
 *   - `QUARANTINE_REGISTRY_NUMBER` env var (default: 87) — issue
 *     number to read the registry from. Set on CI from a repo
 *     variable so the workflow is portable across forks.
 *   - `GH_TOKEN` — required for the `gh issue view` call on CI.
 *     Locally any token with read access to the repo works.
 *
 * Output:
 *   - Exit 0 + one-line "clean" summary when in sync.
 *   - Exit 1 + structured drift report when out of sync.
 *
 * The pure helpers (`scanMarkers`, `parseRegistry`, `compareRegistry`)
 * are exported for a Vitest companion test at
 * `check-quarantine-registry.test.mjs` — the network-touching CLI
 * orchestration is the only part that needs gh and is exercised by
 * the workflow itself.
 */
import { readFile, readdir } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join, relative, resolve, sep, extname } from "node:path";
import { fileURLToPath } from "node:url";
import { execSync } from "node:child_process";

// ============================================================
// Pure helpers (exported for testing)
// ============================================================

/**
 * Walk a directory tree and return every `.spec.ts` file path. Skips
 * dotfiles, symlinks, and `node_modules`. Returns paths relative to
 * the input `rootDir`, with POSIX separators (consistent across
 * Linux CI runs and Windows dev machines).
 */
export async function listSpecFiles(rootDir) {
  const out = [];
  async function walk(dir) {
    let entries;
    try {
      entries = await readdir(dir, { withFileTypes: true });
    } catch (err) {
      if (err?.code === "ENOENT") return;
      throw err;
    }
    for (const entry of entries) {
      if (entry.name.startsWith(".")) continue;
      if (entry.isSymbolicLink()) continue;
      if (entry.isDirectory() && entry.name === "node_modules") continue;
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        await walk(full);
      } else if (entry.isFile() && full.endsWith(".spec.ts")) {
        out.push(relative(rootDir, full).split(sep).join("/"));
      }
    }
  }
  await walk(rootDir);
  out.sort();
  return out;
}

/**
 * Scan source `text` for quarantine markers and return their
 * locations. A marker is either a `test.fixme(` call or a
 * `@quarantine` tag anywhere in the source (per Playwright's
 * documented quarantine idioms + ADR 0010's convention).
 *
 * For each marker the helper extracts:
 *   - `line` (1-based)
 *   - `testName` if expressible from a `test.fixme("…", …)` /
 *     `test.fixme(\`…\`, …)` form; `null` otherwise (e.g. when the
 *     marker is a `@quarantine` tag without a sibling fixme).
 *   - `ticket` — the first `#<digits>` reference found in the marker
 *     line OR in the five lines preceding it. The convention is a
 *     TODO comment above the marker; the 5-line lookback tolerates
 *     a blank line or a multi-line TODO without locking the format.
 *     `null` if no `#<digits>` is found.
 *
 * Pure / synchronous — takes source text, returns marker records.
 * No file I/O so a unit test can pass synthetic strings.
 */
export function parseMarkersFromSource(text, filePath) {
  const lines = text.split(/\r?\n/);
  const markers = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    // Skip lines that are obviously inside a doc/comment block
    // mentioning the convention (the script itself does that — but
    // we ONLY run it on `.spec.ts` files via listSpecFiles, so a
    // mention in a markdown README is already filtered out at the
    // file-listing layer).
    const fixmeMatch = /\btest\.fixme\s*\(/.test(line);
    const tagMatch = /@quarantine\b/.test(line);
    if (!fixmeMatch && !tagMatch) continue;

    // Extract the test name from the fixme call. Plays well with
    // single quotes, double quotes, and backticks. Returns `null`
    // when the marker has no parenthesized name (e.g. a bare
    // `@quarantine` tag, or `test.fixme()` used as a runtime
    // skip inside another `test()` body — rare).
    let testName = null;
    const nameMatch = line.match(
      /test\.fixme\s*\(\s*(?:async\s*\(.*?\)\s*=>|['"`]([^'"`]+)['"`])/,
    );
    if (nameMatch?.[1]) testName = nameMatch[1];

    // Find a `#<digits>` reference for this marker.
    //
    // PRECEDENCE: preceding lines (closest-first) > the marker
    // line itself. The TODO-above-marker shape is the documented
    // convention; the marker line itself is the fallback for the
    // inline-TODO shape (`test.fixme("…", …); // TODO #N`). The
    // ordering matters because a `#N` substring inside the test
    // NAME literal (e.g. `test.fixme("AC #3 …", …)`) would match
    // the wrong ticket if the marker line were preferred. By
    // searching preceding lines first we honor the documented
    // shape and only fall back when the developer used the
    // inline-TODO variant.
    //
    // BACKWARD iteration on preceding lines: a file with two
    // adjacent markers must pick each marker's CLOSEST preceding
    // reference, not the first reference in the file. Forward
    // iteration would have every subsequent marker match the
    // first marker's ticket — every drift report after the first
    // marker would be wrong.
    let ticket = null;
    const searchStart = Math.max(0, i - 5);
    for (let j = i - 1; j >= searchStart; j--) {
      const m = lines[j].match(/#(\d+)\b/);
      if (m) {
        ticket = m[1];
        break;
      }
    }
    // Fallback: same-line `#<digits>` for the inline-TODO shape.
    if (!ticket) {
      const m = lines[i].match(/#(\d+)\b/);
      if (m) ticket = m[1];
    }

    markers.push({
      file: filePath,
      line: i + 1,
      testName,
      ticket,
    });
  }
  return markers;
}

/**
 * Parse the "Quarantine registry" section out of an issue body. Returns
 * one record per checkbox row: `{ ticked: boolean, ticket: string,
 * text: string, lineNo: number }`. The registry block is the contiguous
 * markdown between the `## Quarantine registry` heading and the next
 * `## <heading>` (or end of body). Rows that aren't well-formed are
 * skipped — the script reports them as missing rather than parsing
 * them into a false match.
 *
 * Pure — takes a body string, returns row records. No I/O.
 */
export function parseRegistry(body) {
  const lines = body.split(/\r?\n/);
  let inSection = false;
  const rows = [];
  // Row shape: `- [ ] #<ticket> — …` or `- [x] #<ticket> — …`.
  // The em-dash is the documented separator in #87's row template;
  // we also accept a hyphen ` - ` for tolerance against autocorrect
  // / contributor typo. The ticket number is what we match on, so
  // exact separator shape doesn't matter for the comparison.
  const ROW = /^\s*-\s*\[([ x])\]\s*#(\d+)\b/;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^##\s+Quarantine registry\b/i.test(line)) {
      inSection = true;
      continue;
    }
    if (inSection && /^##\s+/.test(line)) break;
    if (!inSection) continue;
    const m = line.match(ROW);
    if (m) {
      rows.push({
        ticked: m[1].toLowerCase() === "x",
        ticket: m[2],
        text: line.trim(),
        lineNo: i + 1,
      });
    }
  }
  return rows;
}

/**
 * Compare the scanned markers against the parsed registry rows.
 * Returns `{ missing, stale, noTicket }`:
 *
 *   - `missing`: marker in tree but NO matching `- [ ]` row in
 *     registry (the developer skipped step (c) of the Flake policy).
 *     Also includes the case where the matching row IS ticked but
 *     the marker is still in source (ambiguous — either re-tick the
 *     row or remove the marker).
 *   - `stale`: unticked `- [ ]` row in registry whose ticket has no
 *     matching marker in tree (the test was un-`fixme`'d but the
 *     row wasn't ticked).
 *   - `noTicket`: marker in source with no `#<digits>` reference
 *     within the 5-line lookback. The convention REQUIRES a ticket
 *     reference; without one the script can't match the marker to a
 *     row.
 *
 * Pure — synchronous, no I/O.
 */
export function compareRegistry(markers, rows, registryIssue) {
  const missing = [];
  const stale = [];
  const noTicket = [];

  for (const m of markers) {
    if (!m.ticket) {
      noTicket.push(m);
      continue;
    }
    const row = rows.find((r) => r.ticket === m.ticket);
    if (!row) {
      missing.push({
        ...m,
        reason:
          `no row in #${registryIssue} for ticket #${m.ticket} (append:` +
          ` "- [ ] #${m.ticket} — \`${m.file}\`:\`${m.testName ?? "(name)"}\`` +
          ` — quarantined YYYY-MM-DD")`,
      });
    } else if (row.ticked) {
      missing.push({
        ...m,
        reason:
          `row for ticket #${m.ticket} in #${registryIssue} is ticked` +
          ` [x] but the marker is still present in source. Either` +
          ` un-tick the row to [ ] or remove the @quarantine/` +
          `test.fixme marker.`,
      });
    }
  }

  for (const row of rows) {
    if (row.ticked) continue;
    const marker = markers.find((m) => m.ticket === row.ticket);
    if (!marker) {
      stale.push({
        ...row,
        reason:
          `unticked row in #${registryIssue} but no @quarantine/` +
          `test.fixme marker found for ticket #${row.ticket}. ` +
          `Tick the row to [x] (test was fixed) or restore the ` +
          `marker if the row is intentional.`,
      });
    }
  }

  return { missing, stale, noTicket };
}

/**
 * Format a drift report for human + CI consumption. Returns a string.
 * Empty string when no drift.
 */
export function formatDriftReport({ missing, stale, noTicket }, registryIssue) {
  if (missing.length === 0 && stale.length === 0 && noTicket.length === 0) {
    return "";
  }
  const out = [];
  out.push("");
  out.push(`Quarantine-registry drift detected (registry: #${registryIssue}):`);
  out.push("");
  if (noTicket.length > 0) {
    out.push("  Markers in source with NO `#<ticket>` reference in the");
    out.push("  five preceding lines or the marker line itself:");
    for (const m of noTicket) {
      out.push(`    ${m.file}:${m.line}`);
      out.push(`      test: ${m.testName ?? "(no parenthesized name)"}`);
      out.push(
        `      Fix: add a TODO comment above the marker referencing` +
          ` the bug-labelled ticket (per ADR 0010 §Flake policy).`,
      );
    }
    out.push("");
  }
  if (missing.length > 0) {
    out.push("  Markers in source with NO matching `- [ ]` row in the registry:");
    for (const m of missing) {
      out.push(`    ${m.file}:${m.line}`);
      out.push(`      test: ${m.testName ?? "(no parenthesized name)"}`);
      out.push(`      ${m.reason}`);
    }
    out.push("");
  }
  if (stale.length > 0) {
    out.push("  Unticked rows in registry with NO matching marker in source:");
    for (const r of stale) {
      out.push(`    #${registryIssue} line ${r.lineNo}: ${r.text}`);
      out.push(`      ${r.reason}`);
    }
    out.push("");
  }
  out.push(
    `Run the three Flake-policy steps from ADR 0010 in order: (a) tag` +
      ` with @quarantine/test.fixme, (b) file a bug-labelled ticket,` +
      ` (c) append a row to #${registryIssue}. Steps (a) and (c) are` +
      ` what this script enforces.`,
  );
  return out.join("\n");
}

// ============================================================
// CLI entrypoint — fetches #87 via `gh`, scans, prints, exits.
// ============================================================

/**
 * Fetch issue body via `gh issue view --repo <owner/repo>`. Throws
 * when gh is unavailable or the issue can't be read. The shell-out
 * keeps the script close to the bugfix-epic-sync.yml pattern that
 * already uses `gh`.
 *
 * `--repo` is passed explicitly rather than relying on gh's git-
 * remote auto-detection: a nested `.git` directory inside the
 * working tree (e.g. a leftover from a previous setup) takes
 * precedence over the project root and surfaces as "no git remotes
 * found" even when the outer repo is configured correctly. Sourcing
 * the repo from `GITHUB_REPOSITORY` (CI) / `GH_REPO` (local override)
 * sidesteps the foot-gun. Defaults to `neboxdev/complidrop` so the
 * script Just Works on a fresh local checkout without env config.
 */
function fetchIssueBody(issueNumber) {
  const repo =
    process.env.GITHUB_REPOSITORY?.trim() ||
    process.env.GH_REPO?.trim() ||
    "neboxdev/complidrop";
  let raw;
  try {
    raw = execSync(
      `gh issue view ${issueNumber} --repo ${repo} --json body --jq .body`,
      {
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"],
      },
    );
  } catch (err) {
    const stderr = err?.stderr?.toString?.() ?? "";
    throw new Error(
      `Failed to fetch issue #${issueNumber} from ${repo} via gh CLI. ` +
        `Is gh installed and authenticated, and is the repo name ` +
        `correct? Original error: ${err?.message ?? "unknown"}\n${stderr}`,
    );
  }
  return raw;
}

async function main() {
  const registryIssue =
    process.env.QUARANTINE_REGISTRY_NUMBER?.trim() || "87";
  const e2eRoot = process.env.E2E_ROOT?.trim() || "e2e";

  if (!existsSync(e2eRoot)) {
    console.error(
      `check-quarantine-registry: e2e root '${e2eRoot}' does not exist.` +
        ` Run from the frontend/ directory or set E2E_ROOT.`,
    );
    process.exit(1);
  }

  // Walk the spec tree.
  const specFiles = await listSpecFiles(e2eRoot);
  const markers = [];
  for (const rel of specFiles) {
    const text = await readFile(join(e2eRoot, rel), "utf8");
    const fileMarkers = parseMarkersFromSource(text, `${e2eRoot}/${rel}`);
    markers.push(...fileMarkers);
  }

  // Fetch the registry body.
  const body = fetchIssueBody(registryIssue);
  const rows = parseRegistry(body);

  // Compare.
  const drift = compareRegistry(markers, rows, registryIssue);
  const report = formatDriftReport(drift, registryIssue);

  if (report) {
    console.error(report);
    process.exit(1);
  }

  console.log(
    `check-quarantine-registry: clean — ${markers.length} marker(s) in` +
      ` source, ${rows.length} row(s) in #${registryIssue}, no drift.`,
  );
  process.exit(0);
}

// CLI entrypoint guard: only run main() when this file is invoked
// directly (not when imported by the companion test). The idiomatic
// ESM "is this the main module" check resolves the script path
// through node:path/resolve so a relative `node e2e/scripts/foo.mjs`
// invocation matches a resolved `import.meta.url`. Guard against
// `process.argv[1]` being undefined (the dynamic-import-from-another-
// script case) so the module stays importable.
const isMain =
  process.argv[1] &&
  resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  main().catch((err) => {
    console.error(`check-quarantine-registry: ${err?.message ?? err}`);
    process.exit(1);
  });
}

// Re-export node:path/extname for the rare future caller; currently
// unused but kept stable so a third-party importer can use the pure
// helpers without dragging in node:path themselves.
export const _internals = { extname };
