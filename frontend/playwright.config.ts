/**
 * Playwright E2E config — policy decisions documented in
 * `docs/adr/0010-frontend-e2e-with-playwright.md` (ticket #38). Change
 * the ADR alongside any change here that bends one of the policy
 * choices.
 *
 * Smoke-only by design today: tier-1 launch flows live in #39, the
 * rest are deferred to #40.
 *
 * Network policy: every backend route is mocked via `page.route()`
 * inside individual tests (see `e2e/support/mock-api.ts`). The Next
 * dev server boots locally to serve the SPA; ALL `/api/*` calls are
 * intercepted. No live Stripe / Resend / Document AI / Gemini calls
 * leave the CI runner.
 */
import { defineConfig, devices } from "@playwright/test";

const PORT = Number(process.env.PLAYWRIGHT_PORT ?? 3100);
const BASE_URL = `http://localhost:${PORT}`;
const IS_CI = !!process.env.CI;

export default defineConfig({
  testDir: "./e2e",
  // Co-located helpers / fixtures shouldn't be matched as tests.
  testMatch: /.*\.spec\.ts$/,

  // Per-test timeout (ADR 0010 §Flake policy). Bumped to 60s on CI for
  // headless-cold-start latency; 30s locally is fine for an active dev.
  timeout: IS_CI ? 60_000 : 30_000,
  // Per-assertion timeout for `expect.toPass`, `toHaveText`, etc.
  expect: { timeout: 5_000 },

  // CI: retries=1 (ADR 0010 §Flake policy). One retry absorbs the most
  // common transient failures (cold-jit, port-bind, dev-server warmup)
  // without becoming a flake firehose. Locally retries=0 so devs fix
  // the test instead of hiding the flake.
  retries: IS_CI ? 1 : 0,

  // Workers: 1 on CI to avoid port collision with the single
  // webServer; full parallelism locally for fast feedback.
  workers: IS_CI ? 1 : undefined,
  fullyParallel: !IS_CI,

  // Bail early in CI so a runaway loop doesn't burn budget — locally
  // run everything for the diagnostic value.
  forbidOnly: IS_CI,

  // Artifacts (ADR 0010 §Artifact hygiene). All retained ONLY on
  // failure; on success Playwright deletes them and CI never uploads.
  // The secret-scan gate in CI runs against this directory before
  // upload as a belt-and-suspenders check.
  outputDir: "./test-results",

  use: {
    baseURL: BASE_URL,
    // `retain-on-failure` produces trace.zip ONLY for failed tests.
    // The CI scan-secrets job inspects this directory before any
    // artifact upload (see frontend-ci.yml).
    trace: "retain-on-failure",
    // Screenshots on failure only; videos OFF (traces are the
    // diagnostic primary, videos add cost without much extra signal).
    screenshot: "only-on-failure",
    video: "off",

    // Lock down the network. Tests that need any backend call use
    // `mockApi(page, handlers)` from `e2e/support/mock-api.ts`, which
    // installs a `page.route('**/api/**', ...)` interceptor. The
    // `webServer` below serves the SPA; nothing else is allowed.
    ignoreHTTPSErrors: false,

    // Browser context defaults — minimal viewport that catches most
    // layout regressions without forcing unrealistic pixel-perfect
    // assertions.
    viewport: { width: 1280, height: 800 },
  },

  // One browser today (Chromium). #40 may add Firefox/WebKit if smoke
  // coverage proves stable; smoke at one browser is the documented
  // bar.
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],

  // Boot the Next dev server before the tests run. `reuseExistingServer`
  // lets a dev iterate without rebooting between runs; CI always starts
  // fresh (no existing server).
  webServer: {
    command: `npm run dev -- --port ${PORT}`,
    url: BASE_URL,
    reuseExistingServer: !IS_CI,
    timeout: 120_000,
    // Pipe the dev server logs through Playwright's stdio so CI logs
    // show server startup messages alongside test output.
    stdout: "pipe",
    stderr: "pipe",
    // Strict zero-network-out: keep the dev server's API base pointing
    // at a port nothing actually listens on, so any test that forgets
    // to install a mock fails LOUDLY against a connection-refused
    // rather than silently making a live HTTP call to a real origin.
    env: {
      NEXT_PUBLIC_API_URL: "http://127.0.0.1:1",
    },
  },

  // CI reporter prints a summary + writes HTML/JSON; local reporter
  // is the default (list).
  reporter: IS_CI
    ? [
        ["list"],
        ["html", { open: "never", outputFolder: "./test-results/html" }],
        ["json", { outputFile: "./test-results/results.json" }],
      ]
    : "list",
});
