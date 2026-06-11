# 0020. Stripe webhook dedupe is at-least-once with idempotent handlers

- **Status:** accepted
- **Date:** 2026-06-11
- **Deciders:** Ruben G., Claude

## Context

`POST /api/billing/webhook` dedupes Stripe events via a `ProcessedStripeEvent` row keyed
on the event id. Until #268 the row was inserted and **committed before** the handler ran.
A transient handler failure (DB blip, or the outbound Stripe call inside
`FetchPriceIdFromSubscription`) left the event marked processed; Stripe's retry hit the
dedupe check and got a 200, so the event was lost forever. Worst case:
`checkout.session.completed` dropped — the customer paid, the org stayed on the free
5-document cap, and the frontend's `?upgraded=true` toast masked the failure.

The dedupe write and the handler's side effects must not be separable in the failure
direction "marked processed, effects missing".

## Decision

The webhook endpoint **handles first and records after**:

1. Fast-path 200 if the event id already exists (`AnyAsync` dedupe check).
2. Run `HandleWebhookEventAsync`. A throw propagates as a 5xx — the event id is never
   recorded, so Stripe's retry re-runs the handler.
3. Record `ProcessedStripeEvent` only after the handler returns. A unique-violation
   (Postgres 23505) on this insert means a concurrent delivery of the same event won the
   race after its own successful handler run — absorbed as success (200), not a 500.

This makes webhook processing **at-least-once**. The correctness obligation moves into a
contract on `IStripeService.HandleWebhookEventAsync` (documented as XML doc on the
interface): **every handler must be an idempotent state-upsert per event** — re-applying
the same event yields the same row state. Never increments, appends, or one-shot side
effects (e.g. a future dunning email on `invoice.payment_failed` must NOT be sent
directly from a handler; route one-shot effects through their own deduped outbox).
`StripeWebhookTests.Reapplying_an_already_applied_event_yields_the_same_state` pins the
contract.

## Consequences

### Positive
- A transient failure can no longer permanently drop a paid checkout (or any event);
  Stripe's retry schedule (~3 days) becomes the recovery mechanism.
- The crash window between handler success and the dedupe insert resolves to a benign
  re-apply on retry.
- No DB transaction is held open across the outbound Stripe API call in the
  checkout-completed handler.

### Negative
- Handlers carry a durable idempotency obligation that a future author could silently
  violate (mitigated by the interface doc + the re-apply contract test).
- A failed-then-retried event widens the out-of-order window from delivery jitter to
  Stripe's multi-day retry schedule. Handlers are idempotent per event but not
  order-resilient across events — a stale retried `checkout.session.completed` after a
  `customer.subscription.deleted` can resurrect paid state. Tracked as its own
  data-semantics decision in #275.

### Neutral
- Concurrent duplicate deliveries may run the handler twice (same values re-applied);
  the loser of the dedupe insert is absorbed via the 23505 catch.

## Alternatives considered

### Option A — record the event id in the same transaction as the handler's side effects
Wrap dedupe insert + handler in one `BeginTransactionAsync`. Rejected: (a) it only works
if the endpoint and the handler share one `DbContext` — true in prod today (both scoped),
but an implementation detail behind `IStripeService` that the endpoint would silently
depend on, and already false in the test harness where `FakeStripeService` delegates
through a fresh DI scope; (b) it would hold a Postgres transaction open across the
outbound `FetchPriceIdFromSubscription` Stripe call; (c) the existing idempotency
precedent (`Checkout`'s `IdempotencyService`) is also execute-then-store, not
atomic-with-effects.

### Option B — keep record-first and add a "failed" status column
Mark the row pending, flip to processed after the handler, and let retries through on
pending rows. Rejected: reimplements at-least-once with more state and a new
stuck-pending failure mode; the dedupe row's absence already encodes "not done".

## References

- Tickets: #268 (ordering fix), #275 (out-of-order follow-up)
- Code: `BillingEndpoints.Webhook`, `IStripeService.HandleWebhookEventAsync` contract doc
- External: Stripe webhook best practices — handle duplicates idempotently, return 2xx
  only after successful processing
