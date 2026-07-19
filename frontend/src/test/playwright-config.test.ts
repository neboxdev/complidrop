/**
 * Meta-test: pins the artifact-hygiene invariants of the Playwright config
 * (`frontend/playwright.config.ts`) that the CI secret-scan gate depends on
 * (ADR 0010 §Artifact hygiene, hardened in #356).
 *
 * Why a meta-test: the `scan-secrets` CI step catches TODAY's leak, but the
 * gate's forward value rests on two config facts that a Playwright upgrade or
 * a careless edit could silently break — and BOTH failure modes are quiet:
 *
 *   1. `captureGitInfo.diff` — defaults to TRUE whenever Playwright detects
 *      CI, which embeds the branch's whole source diff in `results.json`. That
 *      makes the gate scan the PR's own source, so any PR adding a secret-
 *      SHAPED test fixture fails on a false positive (#356: synthetic SSN
 *      fixtures for the Sentry scrubber). Noisy, not silent — but it trains
 *      the reflex to allowlist report files, which IS silent gate-blinding.
 *   2. `outputDir` must stay the directory `scan-secrets.mjs` actually walks.
 *      If they drift apart the scan still exits 0 — on an empty directory —
 *      and the gate passes forever while scanning nothing. Same config-space
 *      pin pattern as `knip-config.test.ts` (#42) / `eslint-config.test.ts`
 *      (#131).
 */
import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

// Vitest runs with cwd = the frontend project root, where both files live.
const CONFIG_PATH = resolve(process.cwd(), "playwright.config.ts");
const PACKAGE_PATH = resolve(process.cwd(), "package.json");

/**
 * Strip full-line comments before asserting, so a match can never come from
 * the prose (the config's own comment block discusses `captureGitInfo.diff`
 * by name). Drops lines whose trimmed form opens a line/block comment or
 * continues one — which is every comment in this file. Deliberately NOT a
 * general-purpose stripper: a naive `//`-to-EOL pass would also eat the
 * `http://localhost` inside the BASE_URL template literal.
 */
function stripComments(source: string): string {
  return source
    .split("\n")
    .filter((line) => {
      const t = line.trim();
      return !t.startsWith("//") && !t.startsWith("*") && !t.startsWith("/*");
    })
    .join("\n");
}

const configSource = stripComments(readFileSync(CONFIG_PATH, "utf8"));

describe("playwright.config.ts artifact-hygiene pins (ADR 0010 / #356)", () => {
  it("disables git-diff capture so the secret scan never scans the PR's own diff", () => {
    // Playwright writes `captureGitInfo.diff` into config.metadata inside
    // test-results/results.json. Leaving it at the CI default re-breaks the
    // #356 false-positive class for every future security-fixture PR.
    expect(configSource).toMatch(/captureGitInfo\s*:\s*\{[^}]*\bdiff\s*:\s*false\b/);
  });

  it("never re-enables diff capture", () => {
    // Belt-and-braces against an edit that adds a second, contradicting entry
    // rather than flipping the one above.
    expect(configSource).not.toMatch(/\bdiff\s*:\s*true\b/);
  });

  it("keeps outputDir pointed at the directory the secret scan actually walks", () => {
    // If these drift, `scan-secrets.mjs` walks an empty/absent directory,
    // exits 0, and the gate passes while inspecting nothing.
    const outputDir = configSource.match(/outputDir\s*:\s*"\.\/([^"]+)"/)?.[1];
    expect(outputDir).toBe("test-results");

    const scripts = (
      JSON.parse(readFileSync(PACKAGE_PATH, "utf8")) as {
        scripts?: Record<string, string>;
      }
    ).scripts;
    expect(scripts?.["test:e2e:scan"]).toContain(`scan-secrets.mjs ${outputDir}`);
  });
});
