# External integration failure-modes audit — 2026-06-22 (#245)

Adversarial audit of every external boundary — Stripe, Resend, Document AI / Gemini (+ the Anthropic
alt), Azure Blob — attacking the IMPLEMENTATION details (ordering, transactionality, retry, terminal
states), not just the "verify signature + dedupe" basics. Method (per epic #235): each failure class
gets a written verdict — a disproof (SAFE) citing the exact code path, or a finding → fix / ticket.

## Result: 1 confirmed gap (ticketed, deferred); everything else SAFE / acceptable.

| # | Class | Verdict | Evidence / ticket |
|---|---|---|---|
| 1 | Stripe event ordering (updated-before-completed; retries reorder) | SAFE | `IsStaleEvent` fence on `Subscription.LastStripeEventAt` skips events created before the newest applied; checkout fetches LIVE Stripe truth to block resurrection (ADR 0023, #275) |
| 2 | Stripe dedupe transactionality (crash between handler & dedupe) | SAFE | at-least-once + idempotent upsert handlers (ADR 0020): dedupe (`ProcessedStripeEvent`) recorded AFTER the handler; the crash window re-applies benignly; concurrent same-id 23505 absorbed |
| 3 | Subscription lapse behavior (terminal payment failure) | SAFE / intended | grace during Stripe dunning (`past_due`/`unpaid` keep Pro), drop to free on `subscription.deleted` (portal off, cap 5); entitlements gate on flags, fail-closed on missing row — **written down in ADR 0024** + `FreePlanFenceTests` |
| 4 | Resend bounce/complaint suppression | **GAP → [#340](https://github.com/neboxdev/complidrop/issues/340)** | the mark lands only on the per-send `ReminderLog` row; no address-level suppression, so the worker keeps sending to a dead vendor address on future cycles, with no operator visibility. Delivery backstopped only by Resend's own suppression |
| 5 | Resend delivery-status webhook (Svix, out-of-order/redelivery) | SAFE | `ReminderStatusPrecedence` atomic conditional `ExecuteUpdate` (negative-wins, no-rollback, idempotent under redelivery, race-free via Read-Committed re-evaluation) |
| 6 | Extraction provider failures (5xx/timeout/safety-block/malformed JSON) | SAFE | deterministic failures (`MAX_TOKENS`, content-block) → `NonRetryableExtractionException` (fail fast); transient → bounded retries (`MaxAttempts=5`); never stuck `Processing` (per-attempt timeout + zombie reclaim + `MaxClaims`, #259/#243); terminal `Failed` visible |
| 7 | Azure Blob consistency (orphan blob / row-without-blob) | SAFE / acceptable | the dangerous inverse (row without blob) cannot occur — the row is added only after a successful upload; a rare post-upload `SaveChanges` failure leaves a harmless orphan blob (no dangling reference) |
| 8 | HTTP resilience hygiene (timeouts, bounded retries, log-not-Sentry) | SAFE | every external client has an explicit timeout (google/anthropic 2 min, resend 30 s, blob fail-fast `MaxRetries=2`); expected provider failures are `LogError`/`LogWarning`, not thrown to Sentry |

The default integration harness uses fakes that can inject specific failures; the SAFE verdicts below
cite either source-level reasoning or the targeted tests that drive the chaos path (`StripeService`
order/idempotency suites, `ReminderStatusPrecedence` exhaustive cross-check, `BlobStorageServiceTests`,
`ExtractionWorkerTests` #259 timeout/non-retryable paths).

---

## 1–2. Stripe ordering + dedupe transactionality

`StripeService` + `BillingEndpoints.Webhook`. Two hardening layers, both already designed:

- **At-least-once + idempotent (ADR 0020, #268).** The webhook verifies the signature, checks
  `ProcessedStripeEvents.AnyAsync(Id)`, runs `HandleWebhookEventAsync`, THEN inserts the dedupe row.
  The dedupe is deliberately NOT in the same transaction as the side effects: a crash between handler
  success and the insert simply lets Stripe re-deliver, and every handler is a pure state-upsert
  (re-applying the same event yields the same row), so the re-apply is benign. A concurrent same-id
  delivery that races the insert is absorbed (`23505` caught → `Results.Ok`), never 500ing Stripe into
  a spurious extra retry. Verdict: dedupe-after-handler is the correct choice for at-least-once.
- **Order-resilience fence (ADR 0023, #275).** At-least-once widened the reorder window to Stripe's
  ~3-day retry schedule. `IsStaleEvent(sub, eventCreated)` skips any subscription-mutating event created
  strictly before `Subscription.LastStripeEventAt`; every applied event stamps the fence. The checkout
  handler additionally fetches LIVE Stripe truth (`FetchSubscriptionAsync`) and lands free-tier + a
  post-death fence when the minted subscription is already `canceled`/`incomplete_expired` — closing the
  "stale checkout retry resurrects a canceled sub" side door. Backfills identity links (`??=`) even on a
  stale checkout so later `customer.subscription.*` events can still resolve the row. Ties (same-second
  `created`) re-apply by design so the crash-window re-delivery and a same-second checkout+created pair
  both land. **SAFE.**

## 3. Subscription lapse behavior

`invoice.payment_failed` only logs (no state change). During Stripe's dunning the subscription rides as
`past_due`/`unpaid`: `ApplySubscriptionStateAsync` records `Status` but leaves `DocumentLimit`/`HasVendorPortal`
untouched — so the org keeps Pro access (grace) while Stripe retries the card. Only `customer.subscription.deleted`
flips to free-tier (`Plan=free`, `DocumentLimit=5`, `HasVendorPortal=false`). Entitlements gate on those
flags (never `Plan=="pro"`), fail-closed on a missing Subscription row. This is the **intended, written-down**
behavior — ADR 0024 ("Paid entitlements gate on Subscription flags; portal lapse is neutral and reversible"),
pinned by `FreePlanFenceTests`. **SAFE.**

## 4. Resend bounce/complaint  ⟶  [#340](https://github.com/neboxdev/complidrop/issues/340)

`ReminderEndpoints.ResendWebhook` records a `bounced`/`complained` event onto the matching `ReminderLog`
row only (by `ResendMessageId`, via the `ReminderStatusPrecedence` rule). There is **no address-level
suppression**: `ReminderBackgroundService` builds each send's recipients from `doc.Vendor.ContactEmail`
+ internal users with no check against a prior bounce/complaint to that address, so a dead vendor email
keeps receiving future reminders (other docs / later cycles). Delivery is backstopped only by Resend's
own account-level suppression (implicit, outside app control), and — the real product harm — a bounced
**vendor** address surfaces NO operator signal (the #184 dead-letter warning covers only *unverified
internal* recipients), so the "remind → vendor uploads renewal" loop fails silently and the document
expires uncollected. **Filed as [#340](https://github.com/neboxdev/complidrop/issues/340)** (`bug`, → #48),
deferred: app-level suppression + operator visibility is a feature needing product/compliance decisions
(bounce vs complaint permanence, scope, where to surface) — its own `/start` ticket, with the proving
test (a bounced recipient is not re-sent next tick) landing alongside the fix.

## 5. Resend delivery-status webhook (Svix)

`ReminderStatusPrecedence` + the atomic conditional `ExecuteUpdate` in `ResendWebhook`. Svix gives no
ordering guarantee and may redeliver; the handler maps the incoming status to a "current statuses to
ignore" block list and issues `UPDATE … WHERE Status NOT IN (block list)`, which is idempotent under
redelivery (same status → 0 rows), ordering-aware (a late lower-rank positive or positive-after-negative
→ 0 rows, no regression), and race-free under concurrent deliveries (Postgres serializes the row updates
and re-evaluates the second WHERE against the first's committed value). Negative (bounce/complaint) wins
over any state. Signature verified on the raw body before any parse (Svix; fail-closed outside Development).
The `ShouldApply` ↔ `CurrentStatusesToIgnore` agreement is exhaustively unit-tested. **SAFE.**

## 6. Extraction provider failures

`GeminiExtractionClient` / `AnthropicExtractionClient` / `DocumentAiOcrService` + `ExtractionWorker`.
Failures are classified (#259): an HTTP 5xx/4xx throws a retryable `HttpRequestException`; a deterministic
failure that a byte-identical retry can't fix — `MAX_TOKENS` truncation, a non-`STOP` content block
(`SAFETY`/`RECITATION`/…) — throws `NonRetryableExtractionException` and fails immediately (no budget
burn); an odd-but-possibly-transient shape (malformed JSON despite the response schema, `STOP` with no
parts) stays retryable. Transient retries are bounded by `MaxAttempts=5`; a doc never stays `Processing`
forever (per-attempt timeout < the 300 s zombie window + `MaxClaims` backstop, verified in the #243
concurrency audit); the terminal `Failed` status is user-visible with a `ProcessingError`. A cost-ceiling
hit mid-queue fails the doc cleanly (`extraction.cost_ceiling_hit`). Minor note (not a bug): retries are
spaced by the 5 s poll, not exponential backoff — acceptable since the budget is small and the outcome is
a visible terminal state the user can re-extract. **SAFE.**

## 7. Azure Blob consistency

`BlobStorageService` is hardened (#248/#259/#254): construction makes no network call (lazy container
create with retry-after-failure), fail-fast retry (`MaxRetries=2`, short backoff, 30 s per-try timeout),
upload failures map to `BlobStorageUnavailableException` → friendly 503, download 404 → `null`. On the
ingest paths a blob is uploaded BEFORE the DB row is added, so the dangerous inverse — a `Document` row
pointing at a missing blob — cannot arise from a failed ingest. The residual is the reverse: a rare
post-upload `SaveChanges` failure leaves an **orphan blob** (blob with no row). That is **acceptable** —
it has no dangling reference and no correctness/security impact, only negligible storage cost. (Contrast
`SampleEndpoints`, which DOES roll back its blob: that path's `IX_Documents_OrganizationId_SampleUnique`
race is a *common, expected* concurrent-double-click failure warranting cleanup, not the plain upload's
rare DB blip.) A future blob-GC sweep is the natural hardening if storage cost ever matters — not needed
pre-scale. **SAFE / acceptable.**

## 8. HTTP resilience hygiene

Every external client carries an explicit timeout (`AddHttpClient`: google & anthropic 2 min, resend
30 s; blob fail-fast `MaxRetries=2` + 30 s network timeout), so no call can hang indefinitely, and the
per-attempt extraction CTS bounds the whole worker attempt. Expected provider failures are logged as
signal (`LogError`/`LogWarning`) and converted to typed domain outcomes, not rethrown into Sentry noise;
genuine cancellations propagate. No hot retry loop — the worker requeues through the 5 s poll, not a tight
spin. **SAFE.**

## Tests

No production behavior shipped (the one finding is deferred to #340 with its proving test), so the SAFE
verdicts rest on the existing suites, confirmed not assumed: `StripeService`/`BillingEndpoints` order +
idempotency + lifecycle tests (ADR 0020/0023, `FreePlanFenceTests`), `ReminderStatusPrecedence`'s
exhaustive `ShouldApply` ↔ `CurrentStatusesToIgnore` cross-check + the signed-webhook tests,
`BlobStorageServiceTests` (fail-fast + 503 mapping + retry-after-failure), and `ExtractionWorkerTests`
(#259 non-retryable + timeout + cost-ceiling + terminal-status paths).
