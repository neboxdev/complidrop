# Frontend E2E suite

Playwright + Chromium, network-mocked by default. The full rationale lives in [ADR 0010](../../docs/adr/0010-frontend-e2e-with-playwright.md); this README is the day-to-day reference.

## One-command run

```bash
# First-time setup — installs the Chromium binary (~150 MB):
cd frontend && npx playwright install chromium

# Local (interactive, opens the HTML report on failure):
npm run test:e2e

# CI-equivalent (one worker, retries=1, JSON + HTML reporters):
npm run test:e2e:ci
```

The Playwright config (`playwright.config.ts`) boots the Next dev server on `http://localhost:3100` via `webServer.command = "npm run dev -- --port 3100"`. `reuseExistingServer: true` locally so iterating is fast; CI always starts fresh.

## Layout

```
frontend/e2e/
  smoke/                  Tier-1 launch flows (see #39). Today: harness.spec.ts only.
  support/
    mock-api.ts           page.route('**/api/**', …) interceptor + jsonOk/jsonError.
    fixtures.ts           E2E-side typed shapes (authedMe, portalInfo, …).
  scripts/
    scan-secrets.mjs      Secret-scan gate (Node, no deps). Runs in CI after Playwright.
  README.md               This file.
```

## Network policy (zero live calls)

Every `/api/**` request must be matched by a `mockApi()` route, or the test fails with a 404 (`test.no_mock` envelope). The Next dev server's `NEXT_PUBLIC_API_URL` is pinned to `http://127.0.0.1:1` so any code path that forgets to install a mock fails LOUDLY with `ECONNREFUSED` rather than silently leaking to a real origin.

```ts
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk, jsonError } from "../support/mock-api";
import { authedMe } from "../support/fixtures";

test("logged-out home page", async ({ page }) => {
  await mockApi(page, [
    {
      method: "GET",
      path: "/api/auth/me",
      handler: jsonError("auth.unauthorized", "Not authenticated", 401),
    },
  ]);
  await page.goto("/");
  // ...
});
```

The list is matched in declared order; first prefix-match wins. Use `:token` / `:id` for path-param wildcards (e.g. `path: "/api/portal/:token"`).

## Artifact hygiene

- **Trace:** `retain-on-failure`. Trace files (`trace.zip`) only exist for failing tests; the CI workflow scans them with `scan-secrets.mjs` BEFORE upload and uploads only the per-failure subset.
- **Screenshot:** `only-on-failure`. Same scrutiny as traces.
- **Video:** `off`. Traces carry the diagnostic signal at lower cost.
- **`test-results/`** is gitignored; never commit Playwright artifacts.

## Secret-scan gate

The `scan-secrets.mjs` script runs in CI after every Playwright job and fails the build if any artifact contains:

- `cd_session=` / `cd_refresh=` (CLAUDE.md's httpOnly JWT cookies — must never appear in a trace)
- `Authorization: Bearer` / `Authorization: Basic` headers
- `portal-token:` headers with a value
- SSN-shaped patterns (`\d{3}-\d{2}-\d{4}`)
- EIN-shaped patterns (`\d{2}-\d{7}`)

If a finding is a documented false-positive (e.g. a test that intentionally renders a redacted SSN), add the file path to the `ALLOWLISTED_FILES` array in the script with a one-line comment.

The script is also invoked by `pretest:e2e` so devs catch leaks locally before push.

## Flake policy

- **Retries:** 1 in CI, 0 locally. Locally a flake = a bug; CI absorbs the most common transient causes (port bind, dev-server warmup) once.
- **Per-test timeout:** 30s local, 60s CI.
- **Per-assertion timeout:** 5s.
- **Red E2E blocks merge.** No skip-without-ticket; flaky tests are quarantined behind the `@quarantine` tag (Playwright `test.fixme` with a TODO referencing the ticket) and the ticket joins the rolling bug-fix epic [#48](https://github.com/neboxdev/complidrop/issues/48).

## Updating the fixture set

Mirrors `frontend/src/test/fixtures.ts` (Vitest harness). Keep the two in sync at the shape level — drift is fine for additive fields but renames must update both.

The backend [`api/CompliDrop.Api.Tests/ExtractionFixtures/`](../../../api/CompliDrop.Api.Tests/ExtractionFixtures/) is confirmed synthetic per its own README — but this E2E suite does NOT import them. Document-detail / extraction-status fixtures live inline in the test that needs them, sized to match the Vitest `makeDocumentDetail` factory.

## Adding a new smoke test (preview of #39)

1. Create a `.spec.ts` file under `frontend/e2e/smoke/`.
2. Import `mockApi` + the relevant fixtures.
3. Declare every `/api/*` your flow hits — the harness fails LOUDLY on unmocked routes.
4. Run `npm run test:e2e -- smoke/your-flow.spec.ts` to iterate.
5. Add the flow to the smoke set documented in [ADR 0010](../../docs/adr/0010-frontend-e2e-with-playwright.md).
