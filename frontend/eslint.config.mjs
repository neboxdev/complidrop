import { defineConfig, globalIgnores } from "eslint/config";
import nextVitals from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

const eslintConfig = defineConfig([
  ...nextVitals,
  ...nextTs,
  // Override default ignores of eslint-config-next.
  globalIgnores([
    // Default ignores of eslint-config-next:
    ".next/**",
    "out/**",
    "build/**",
    "next-env.d.ts",
  ]),
  // Project-specific rule overrides.
  // jsx-a11y/label-has-associated-control: enforce the label-wiring contract
  // from #76 (every <Label htmlFor=...> targets an input with the same id, or
  // nests the control). eslint-config-next only enables a "warn"-tier subset of
  // jsx-a11y rules; this one ships off-by-default but is mandatory here.
  // See #131.
  {
    name: "complidrop/jsx-a11y",
    files: ["**/*.{js,jsx,mjs,ts,tsx,mts,cts}"],
    rules: {
      "jsx-a11y/label-has-associated-control": "error",
    },
  },
]);

export default eslintConfig;
