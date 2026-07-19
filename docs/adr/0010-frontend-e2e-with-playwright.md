# 0010. Frontend E2E with Playwright (network-mocked, scrubbed artifacts, conservative flake policy)

- **Status:** accepted
- **Date:** 2026-05-26
- **Deciders:** Ruben G.

## Context

[ADR 0003](0003-frontend-testing-with-vitest.md) settled the unit/component testing layer (Vitest + RTL + jsdom) and explicitly deferred end-to-end choice ("if cross-page flows need coverage later, that's a separate decision/ADR"). Epic [#33](https://github.com/neboxdev/complidrop/issues/33) (frontend test hardening) makes that "later" now — auth, vendor-portal upload, and upload→extraction are exactly the cross-page flows the Vitest layer can't prove. [#34](https://github.com/neboxdev/complidrop/issues/34) shipped the Vitest harness; this ADR fixes the E2E layer that #39 / #40 will populate.

The vendor portal at `/portal/[token]` is the highest-empathy and highest-empathy-cost surface in the product (CLAUDE.md: "PUBLIC, treat inputs as untrusted"; SMB target audience, one-shot upload). It is also the surface most exposed to artifact-leak risk during E2E — a careless trace can capture the httpOnly JWT cookies (`cd_session`, `cd_refresh`) or PII-shaped patterns (SSN/EIN) into a CI artifact. CLAUDE.md's auth invariant is "tokens never leave the server" — that constraint MUST extend to the E2E layer's recordings.

We need an E2E harness that:

1. Boots a single command locally and in CI.
2. Mocks every backend network call (Stripe / Resend / Document AI / Gemini / Postgres / Azure Blob / the CompliDrop API itself). E2E proves frontend wiring, not the full stack; live-stack testing is a separate quality concern out of scope for #38/#39.
3. Strips Set-Cookie / Authorization / portal-token headers from anything captured into artifacts AND has a CI gate that fails the build if a SSN/EIN-shaped pattern or a JWT-cookie name leaks into uploaded artifacts.
4. Carries a flake policy strict enough that a red E2E means SOMETHING is genuinely broken.

## Decision

Use **Playwright + Chromium** (one project, smoke-only).

### Runner choice

- **Tool:** `@playwright/test@^1.60`.
- **Browser:** Chromium only at launch. Firefox/WebKit may be added if and only if smoke coverage proves stable for ≥4 weeks post-launch.
- **One command:** `npm run test:e2e` (local) and `npm run test:e2e:ci` (CI). Both invoke `playwright test`; CI sets `CI=1` so the config's CI branches activate (workers=1, retries=1, JSON+HTML reporters).
- **Spec convention:** files named `*.spec.ts` live under `frontend/e2e/`. Helpers and fixtures use plain `.ts` extensions so the `testMatch` regex excludes them.

### Network policy (zero live calls)

- The Next dev server boots on `http://localhost:3100` (separate from the developer's `npm run dev` port 3000).
- The dev server's `NEXT_PUBLIC_API_URL` is pinned to `http://127.0.0.1:1` so any code path that forgot to install a mock fails LOUDLY with `ECONNREFUSED` rather than silently leaking to a real origin.
- Every `/api/**` request must be matched by a `mockApi()` route. The default catch-all returns a `test.no_mock` envelope so missing mocks surface as a clear test failure, not a timeout.
- No live test ever hits Stripe / Resend / Document AI / Gemini / Postgres / Azure Blob. The full-stack acceptance check is a separate manual run before launch and is documented out-of-band; nothing in this ADR enables it.

### Artifact hygiene

- **Trace:** `retain-on-failure`. Trace files only exist for failing tests.
- **Screenshot:** `only-on-failure`.
- **Video:** `off`. Traces carry the diagnostic signal at lower cost.
- **`test-results/`** is gitignored.
- CI scans `test-results/` with `scan-secrets.mjs` AFTER the Playwright run but BEFORE artifact upload. The scan fails the build on:
  - `cd_session=` / `cd_refresh=` (the CompliDrop httpOnly JWT cookies per CLAUDE.md)
  - `Authorization: Bearer` / `Authorization: Basic` headers
  - `portal-token:` headers with a value
  - SSN-shaped patterns (`\b\d{3}-\d{2}-\d{4}\b`)
  - EIN-shaped patterns (`\b\d{2}-\d{7}\b`)
- Allowlisting goes in `ALLOWLISTED_FILES` inside the script with a one-line comment; no regex carve-outs (carve-outs decay).

**Amendment 1 (#356) — the scan targets RUNTIME artifacts, never the source diff.** Playwright's `captureGitInfo.diff` defaults to `true` whenever it detects CI, which embeds the branch's entire source diff into `config.metadata` inside `test-results/results.json`. That silently widened this gate from "did the running app leak a secret into a trace?" to "does the PR's own source contain a secret-shaped string?" — so a PR adding a synthetic secret-shaped **test fixture** fails the gate with no leak present. #356 hit it: three `123-45-6789` fixtures pinning the Sentry SSN scrubber. Security-hardening PRs add such fixtures by their nature, so it recurs by construction. Resolution: `captureGitInfo: { diff: false }` in [`playwright.config.ts`](../../frontend/playwright.config.ts) (commit info stays — small, useful, no source text), pinned against regression by [`src/test/playwright-config.test.ts`](../../frontend/src/test/playwright-config.test.ts).

**Corollary — never allowlist a whole report file.** `ALLOWLISTED_FILES` is keyed on artifact paths, which is the right granularity for a single fixture file and the WRONG one for `results.json` / the HTML report: those aggregate every test's runtime data, so allowlisting one blinds the gate to real leaks wholesale. When a report file trips the scan, fix what put the string in the report (as above) rather than muting the file.

### Backend ExtractionFixtures

The backend test tree at [`api/CompliDrop.Api.Tests/ExtractionFixtures/`](../../api/CompliDrop.Api.Tests/ExtractionFixtures/) carries synthetic placeholder PDFs and YAML expectations. Per its own README ("placeholders … swap with real PDFs … before running the regression suite against the live extraction pipeline"), the fixtures are synthetic at rest. **The E2E suite does NOT import them.** Document-detail / extraction-status payloads needed by E2E live inline in the test that needs them, modeled on the Vitest harness's `makeDocumentDetail` factory.

### Flake policy

- **Retries:** 1 in CI, 0 locally. Locally a flake is a bug — fix it. CI absorbs the most common transient causes (cold-jit, port-bind, dev-server warmup) once.
- **Per-test timeout:** 30s local, 60s CI.
- **Per-assertion timeout:** 5s (`expect.timeout`).
- **Workers:** 1 in CI to keep CPU/memory bounded on the cheap runner and serialize against the single dev-server's JIT pressure (the bottleneck is the dev server, not the runner). Fully-parallel locally for fast feedback. Earlier drafts of this ADR said "to avoid port collision" — that was wrong (multiple workers share one webServer), corrected on the same PR.
- **Quarantine:** flaky tests are tagged `@quarantine` (Playwright `test.fixme` with a TODO referencing a ticket). The ticket is `bug`-labelled so it joins the rolling bug-fix epic [#48](https://github.com/neboxdev/complidrop/issues/48) until the test is either fixed or deleted. A meta-tracker issue ([#87](https://github.com/neboxdev/complidrop/issues/87)) keeps the current parking lot visible so a flake parked six months ago doesn't decay into "we forgot why this was skipped."
  - **Mechanical check ([#115](https://github.com/neboxdev/complidrop/issues/115)):** the `quarantine-drift` CI step in [`frontend-ci.yml`](../../.github/workflows/frontend-ci.yml) fails the build when (a) a `@quarantine` / `test.fixme` marker in `frontend/e2e/**/*.spec.ts` has no matching `- [ ]` row in #87, or (b) an unticked row in #87 references a ticket whose marker is no longer in the source. Mirrors the ADR 0009 / [#64](https://github.com/neboxdev/complidrop/issues/64) mechanical-enforcement pattern: small Node codebase scan + non-zero exit. Source at [`frontend/e2e/scripts/check-quarantine-registry.mjs`](../../frontend/e2e/scripts/check-quarantine-registry.mjs); fast-tier contract test at [`check-quarantine-registry.test.mjs`](../../frontend/e2e/scripts/check-quarantine-registry.test.mjs).
- **Red E2E blocks merge.** No skip-without-ticket. CI's frontend-ci.yml gates `playwright test` AND the secret-scan as required checks.

## Consequences

### Positive

- Cross-page flows have a real safety net — the layer Vitest cannot prove now has one.
- Network-mocked-by-default + pinned-unreachable backend means the suite cannot be the source of an accidental Stripe charge, a real-tenant data leak, or a Resend test email to a real address.
- The secret-scan gate is mechanical — engineers do not need to remember not to leak cookie values; the build fails if they do.
- Smoke-only set (3 flows in #39, +1 from #90 = 4 authed flows under `frontend/e2e/smoke/`) is bounded; the suite stays fast (target < 90s on CI Chromium-only).
- A single retry policy is conservative enough that a red E2E means SOMETHING is genuinely broken; we don't normalize flakes.

### Negative

- Two test "philosophies" inside the frontend now (Vitest for unit, Playwright for cross-page). ADR 0003 already accepted this trade-off for the backend vs frontend split; this extends it within the frontend. The README distinguishes "use Vitest when X, Playwright when Y" so contributors don't pick wrong.
- Mocked-backend E2E is not a substitute for a real-stack pre-launch smoke. That's a separate manual gate captured in launch docs, not in this ADR.
- Playwright adds ~30 MB of devDependencies + browser binaries. CI caches `~/.cache/ms-playwright/` between runs to amortize.

### Neutral

- Single browser (Chromium) at launch. Firefox/WebKit are a follow-on decision pending evidence; documenting that here so a future contributor knows the gap is intentional, not accidental.
- Single port (3100) for the E2E dev server. If a future test needs a second port (e.g. parallel Stripe-mock server), the port allocation lives in the config, not in individual tests.
- E2E runs against `next dev`, NOT `next build && next start`. Regressions that only manifest in the production bundle (SSR-only paths, optimized chunks, cookie-domain quirks) are NOT caught here — smoke for the production bundle is a separate manual pre-launch step captured in launch docs. Switch the `webServer.command` to `next build && next start` once the smoke set is stable enough that the ~30s extra build time per CI run is a worthwhile trade.
- The `NEXT_PUBLIC_API_URL=http://127.0.0.1:1` safety net (unmocked /api calls fail with ECONNREFUSED) is a CI-only guarantee. Locally, `webServer.reuseExistingServer: true` means a leftover `next dev --port 3100` from a prior session bypasses the pin and inherits `.env.local`. The README documents this; devs running E2E against a stale dev server may see unexpected real-network traffic.

## Alternatives considered

### Option A — Cypress

The other established E2E runner. Rejected:

- Playwright's `route()` interception is more flexible than Cypress's `cy.intercept()` for the patterns this project actually needs (catch-all + path-param matching). The path-param feature alone saves boilerplate in every portal/document-detail test.
- Trace-viewer is a clear Playwright win for the post-mortem of a CI failure (Cypress runner replay is video-only by default).
- Multi-browser at the same API surface (we're at Chromium-only now, but Firefox/WebKit is one config-line away).
- Playwright is the dominant new-project choice in the Vite/Next/React-19 ecosystem as of 2026, lowering the contribution friction for a solo founder who occasionally accepts outside help.

### Option B — Real-stack E2E against an ephemeral Postgres / Azure Blob

Tempting because it would catch backend regressions too. Rejected:

- Brings the full stack into the test runtime: Postgres (Testcontainers), Azure Blob (Azurite), Document AI / Gemini (paid APIs or hand-rolled mocks anyway), Stripe (test-mode but real charges in the dashboard, real webhooks to verify). Each of those is a separate quality concern with its own existing coverage (backend xUnit + Testcontainers).
- The frontend E2E should be FAST and ISOLATED. The moment we depend on the backend, "the build is red because a Postgres testcontainer didn't start" enters our trouble-shooting tax. Mocking the API client surface at the frontend's edge gives us the wiring coverage we need without that tax.
- Real-stack acceptance is a separate manual pre-launch gate, captured in launch docs.

### Option C — Browser-mode Vitest

In-process E2E inside Vitest. Rejected:

- Vitest's browser mode is the right runner for COMPONENT tests that need a real browser layout engine (we don't need that — jsdom is sufficient for the assertions we care about today).
- It cannot exercise route transitions, real cookie storage, real network round-trips in the way Playwright can, even with everything mocked.
- The artifact / trace story is much weaker; the secret-scan gate would have to grow custom support.

### Option D — Defer E2E to post-launch

Land at launch with Vitest only. Rejected:

- The three flows that gate launch (auth, vendor-portal upload, upload→extraction) are exactly the surfaces a Vitest-only suite can't prove. A bad copy-paste in the auth layout (logged-in user landing on `/login` and getting redirected wrong) would only surface as a customer support email.
- The cost of standing up the harness once and writing three smoke tests (#39) is small (one session each).

## References

- Tickets: [#38](https://github.com/neboxdev/complidrop/issues/38) (this ticket — harness + ADR), [#39](https://github.com/neboxdev/complidrop/issues/39) (smoke E2E flows), [#40](https://github.com/neboxdev/complidrop/issues/40) (remaining E2E flows, deferred post-launch), [#33](https://github.com/neboxdev/complidrop/issues/33) (epic), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic — individual quarantined-test tickets live here), [#87](https://github.com/neboxdev/complidrop/issues/87) (E2E quarantine registry — meta-tracker for tests currently behind `@quarantine`).
- ADRs: [0003](0003-frontend-testing-with-vitest.md) (Vitest + RTL — companion choice).
- Config: `frontend/playwright.config.ts`, `frontend/e2e/`, `frontend/e2e/scripts/scan-secrets.mjs`, `frontend/e2e/README.md`.
- CI: `.github/workflows/frontend-ci.yml` (Playwright job + scan-secrets gate).
- External: Playwright docs ([playwright.dev](https://playwright.dev/)).
