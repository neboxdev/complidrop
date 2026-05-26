import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// Minimal component-test setup: jsdom + React plugin + the same "@/" path alias
// as tsconfig. Scoped to *.test.* files so it never picks up route files or the
// Next build output.
//
// `test.env.NEXT_PUBLIC_API_URL` is pinned here (not in vitest.setup.ts) so the
// value is set BEFORE any test or setup file imports — including transitive
// imports of `frontend/src/lib/api.ts`, whose `API_BASE` is computed once at
// module load via `process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5292"`.
// Setting it in setupFiles works today only because no current setup-file
// import transitively reads the env var; pinning at the runner level removes
// that latent fragility.
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./vitest.setup.ts"],
    include: ["src/**/*.{test,spec}.{ts,tsx}"],
    env: {
      NEXT_PUBLIC_API_URL: "http://localhost:5292",
    },
  },
});
