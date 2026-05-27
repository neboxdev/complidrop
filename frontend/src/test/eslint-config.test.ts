/**
 * Meta-test: pins the `jsx-a11y/label-has-associated-control` lint rule at
 * `'error'` in `frontend/eslint.config.mjs`.
 *
 * Why a meta-test (#131): the rule is the second layer of the label-wiring
 * enforcement story for #76 — `forms.test.tsx` covers runtime wiring on the
 * enumerated forms, and the lint rule catches every future label at CI time.
 * That story collapses to a single layer if a future eslint-config-next
 * upgrade, a config refactor, or a slipped-in `'off'` override silently turns
 * the rule back off. This test fails loudly the moment that happens — same
 * pattern as the `forms.test.tsx` runtime pin, in config space.
 *
 * The check walks the flat-config array and reports the LAST resolved value
 * for the rule (flat-config later-wins semantics). It tolerates the rule
 * being inherited from a preset spread upstream — only the final resolved
 * value matters.
 */
import { describe, it, expect } from "vitest";
// @ts-expect-error - .mjs config has no .d.ts; we only read the rules array.
import eslintConfig from "../../eslint.config.mjs";

type FlatConfigBlock = {
  name?: string;
  rules?: Record<string, unknown>;
};

const RULE = "jsx-a11y/label-has-associated-control";

function resolveRule(config: FlatConfigBlock[]): unknown {
  let resolved: unknown;
  for (const block of config) {
    if (block && typeof block === "object" && block.rules && RULE in block.rules) {
      resolved = block.rules[RULE];
    }
  }
  return resolved;
}

describe("eslint.config.mjs (#131)", () => {
  it(`pins ${RULE} at 'error'`, () => {
    expect(Array.isArray(eslintConfig)).toBe(true);
    const value = resolveRule(eslintConfig as FlatConfigBlock[]);
    // Accepts either the bare 'error' string or ['error', ...options].
    const severity = Array.isArray(value) ? value[0] : value;
    expect(severity).toBe("error");
  });
});
