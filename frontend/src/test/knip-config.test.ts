/**
 * Meta-test: pins the load-bearing invariants of the knip dead-code gate config
 * (`frontend/knip.jsonc`), added with the #42 cleanup-tooling gate.
 *
 * Why a meta-test: the `npm run knip` CI step catches TODAY's dead code, but the
 * gate's forward value depends on config invariants that a knip major-version bump
 * or a careless edit could silently weaken — e.g. someone slipping in a
 * `"rules": { "exports": "off" }` block (which would re-disable the dead-export
 * detection #42 deliberately kept live), or deleting the scoped `ignore` globs that
 * keep the shadcn `ui/` kit + the shared test toolkit from flooding the gate. This
 * test fails loudly the moment that happens — same config-space pin pattern as
 * `eslint-config.test.ts` (#131).
 */
import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import JSON5 from "json5";

// knip.jsonc is JSONC (comments + trailing commas allowed). Parse with json5 — which accepts
// exactly what knip itself tolerates — so this meta-test never false-fails on a benign config
// edit (a block comment, an inline trailing comment, a trailing comma). Vitest runs with
// cwd = the frontend project root, where knip.jsonc lives.
const CONFIG_PATH = resolve(process.cwd(), "knip.jsonc");
const config = JSON5.parse(readFileSync(CONFIG_PATH, "utf8")) as {
  ignore?: string[];
  ignoreDependencies?: string[];
  ignoreBinaries?: string[];
  rules?: Record<string, string>;
};

describe("knip.jsonc gate config (#42)", () => {
  it("scopes out EXACTLY the intentional-public-surface dirs (no tree-swallowing ignore)", () => {
    // Exact equality, not arrayContaining: a future ignore like "src/**" or "**" would gut the
    // live dead-export gate while a superset check stayed green. Any change to this set must
    // therefore be a deliberate edit here.
    expect([...(config.ignore ?? [])].sort()).toEqual(
      ["e2e/support/**", "src/components/ui/**", "src/test/**"].sort(),
    );
  });

  it("does not silently disable any knip finding class", () => {
    // #42 kept every finding class ENFORCED (no rule turned "off"); the only scoping is
    // via the `ignore` globs above. A future "off" must be a deliberate, reviewed edit —
    // which updating this assertion forces.
    const offRules = Object.entries(config.rules ?? {}).filter(
      ([, severity]) => severity === "off",
    );
    expect(offRules).toEqual([]);
  });

  it("keeps the documented dependency/binary ignores", () => {
    expect(config.ignoreDependencies).toEqual(
      expect.arrayContaining(["@sentry/nextjs", "client-only"]),
    );
    expect(config.ignoreBinaries).toEqual(expect.arrayContaining(["gh"]));
  });
});
