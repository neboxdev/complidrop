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

Routes match in declared order; first MATCH wins. Matching is **exact segment-count** with `:param` wildcards — NOT prefix matching. `path: "/api/portal"` does NOT match `/api/portal/abc/info`; declare the full path or use `:token` (e.g. `path: "/api/portal/:token"`). Declare more-specific (literal) paths before wildcards so a `:id` route doesn't accidentally shadow a literal sibling.

## Artifact hygiene

- **Trace:** `retain-on-failure`. Trace files (`trace.zip`) only exist for failing tests; the CI workflow scans them with `scan-secrets.mjs` BEFORE upload and uploads only the per-failure subset.
- **Screenshot:** `only-on-failure`. Same scrutiny as traces.
- **Video:** `off`. Traces carry the diagnostic signal at lower cost.
- **`test-results/`** is gitignored; never commit Playwright artifacts.

## Secret-scan gate

The `scan-secrets.mjs` script runs in CI after every Playwright job (and locally via `posttest:e2e`) and fails the build if any artifact contains:

- `cd_session=` / `cd_refresh=` (CLAUDE.md's httpOnly JWT cookies — must never appear in a trace)
- Any value-bearing `Authorization:` header (Bearer / Basic / bare token — the project doesn't use Authorization at all, so any occurrence is suspicious)
- `/api/portal/{token}` URL paths where the token is ≥16 chars (the production form of vendor-portal tokens)
- SSN-shaped patterns (`\d{3}-\d{2}-\d{4}`)
- EIN-shaped patterns (`\d{2}-\d{7}`)

Playwright's `trace.zip` is a real compressed archive — the scanner uses `adm-zip` to unzip and recursively scan each entry, so the diagnostic file most likely to contain a leak IS covered.

If a finding is a documented false-positive (e.g. a test that intentionally renders a redacted SSN), add the file path to `ALLOWLISTED_FILES` in the script with a one-line comment. Allowlist entries are POSIX-shaped (forward slashes) and normalized at comparison time so Windows runs match Linux CI.

The CI workflow gates the artifact upload on `steps.playwright.outcome == 'failure' && steps.scan.outcome == 'success'` — if the scan finds a leak, the upload is SKIPPED, so a leaked cookie never reaches GitHub's artifact store.

## AC #6 enforcement — no backend imports

`lint-imports.mjs` walks `frontend/e2e/` and fails if any test file imports from `api/CompliDrop.Api*` or references `ExtractionFixtures` in a code-level import statement. Convention via README is not enough; the script makes it mechanical. Runs in CI under `npm run test:e2e:lint-imports`.

## Local dev caveat — the ECONNREFUSED safety net is CI-only

`playwright.config.ts` pins `NEXT_PUBLIC_API_URL=http://127.0.0.1:1` on the dev server it spawns, so any unmocked /api call fails LOUDLY in CI. Locally, `webServer.reuseExistingServer: true` means a leftover `next dev --port 3100` from a previous session is reused — and that server inherited `.env.local`'s real `NEXT_PUBLIC_API_URL`. If you've been running a dev server on port 3100, restart it (or kill it) before running E2E so Playwright spawns a fresh one with the pinned env.

## Flake policy

- **Retries:** 1 in CI, 0 locally. Locally a flake = a bug; CI absorbs the most common transient causes (port bind, dev-server warmup) once.
- **Per-test timeout:** 30s local, 60s CI.
- **Per-assertion timeout:** 5s.
- **Red E2E blocks merge.** No skip-without-ticket; flaky tests are quarantined behind the `@quarantine` tag (Playwright `test.fixme` with a TODO referencing the ticket).
- **Quarantining a test → two actions:**
  1. File a `bug`-labelled ticket capturing the flake symptom + suspected root cause; that ticket auto-joins rolling epic [#48](https://github.com/neboxdev/complidrop/issues/48) via [`bugfix-epic-sync.yml`](../../.github/workflows/bugfix-epic-sync.yml).
  2. Append a one-line row to the **[#87 E2E quarantine tracker](https://github.com/neboxdev/complidrop/issues/87)** body under "Quarantine registry" so the parking lot stays visible — `- [ ] #<ticket> — `<test-file>`:`<test-name>` — quarantined <YYYY-MM-DD>`. Check the row off when the test is fixed or deleted.

## Updating the fixture set

Mirrors `frontend/src/test/fixtures.ts` (Vitest harness). Keep the two in sync at the shape level — drift is fine for additive fields but renames must update both.

The backend [`api/CompliDrop.Api.Tests/ExtractionFixtures/`](../../../api/CompliDrop.Api.Tests/ExtractionFixtures/) is confirmed synthetic per its own README — but this E2E suite does NOT import them. Document-detail / extraction-status fixtures live inline in the test that needs them, sized to match the Vitest `makeDocumentDetail` factory.

## Adding a new smoke test (preview of #39)

1. Create a `.spec.ts` file under `frontend/e2e/smoke/`.
2. Import `mockApi` + the relevant fixtures.
3. Declare every `/api/*` your flow hits — the harness fails LOUDLY on unmocked routes.
4. Run `npm run test:e2e -- smoke/your-flow.spec.ts` to iterate.
5. Add the flow to the smoke set documented in [ADR 0010](../../docs/adr/0010-frontend-e2e-with-playwright.md).

## When NOT to use this harness

- **Component-level assertions** (one component's render, prop, or hook): use the Vitest harness at [`frontend/src/test/`](../test/README.md). Playwright is slower per test and overkill for unit-scope coverage.
- **Hook contract tests** (refresh-on-401, cache key behavior, retry sequencing): use `renderHook` + `vi.stubGlobal("fetch", ...)` inside Vitest — see `src/lib/api.test.ts` for the pattern.
- **Anything that doesn't cross a page boundary or a multi-step user flow.** Playwright is for "go to /login, submit, land on /dashboard, see the welcome" — not "the welcome heading renders when isAuthed is true." The latter is a Vitest job.
- **Production-mode SSR regressions.** The harness runs against `next dev`, not `next build && next start`. Regressions that only present in the production bundle are not caught here; smoke for the production bundle is a separate manual pre-launch step captured in launch docs.
