# 0023. Stripe webhook order-resilience via last-applied-event fence

- **Status:** accepted (extends ADR 0020)
- **Date:** 2026-06-12
- **Deciders:** Ruben G., Claude

## Context

ADR 0020 made webhook processing at-least-once: a failed handler leaves the event
unrecorded and Stripe's retry re-runs it. That widened the out-of-order window from
natural delivery jitter (seconds) to Stripe's ~3-day retry schedule. Handlers were
idempotent per event but not order-resilient across events: a stale retried
`checkout.session.completed` after a `customer.subscription.deleted` re-granted
`Status=active, DocumentLimit=null, HasVendorPortal=true` on a canceled subscription —
and no future event ever corrects it, because the subscription is deleted and Stripe
sends nothing more for it (#275, found in #268's review).

Two candidate semantics were on the table (#275):

1. **Event-ordering fence** — persist the Stripe `created` of the last applied event;
   skip events created strictly earlier.
2. **Live re-fetch** — Stripe's recommended pattern: treat every event as a trigger and
   fetch the subscription's current state from the Stripe API instead of trusting the
   payload snapshot.

## Decision

**Option 1 — a `Subscription.LastStripeEventAt` fence — plus two hardenings, one of
which is Option 2's mechanism applied exactly where an outbound call already existed.**

1. **Fence.** Every *applied* subscription-mutating event (`checkout.session.completed`,
   `customer.subscription.created/updated/deleted`) stamps its `Event.Created` into
   `Subscription.LastStripeEventAt` (nullable timestamptz; null = no event applied yet).
   A handler skips state application when `ev.Created < LastStripeEventAt`
   (`StripeService.IsStaleEvent`). The skip still returns success, so the endpoint
   records the event id and Stripe stops retrying — a stale event is *acknowledged*,
   not *applied*. **Ties apply** (skip is strictly-older only): Stripe `created` is
   second-granularity, equal-timestamp re-delivery is exactly ADR 0020's crash-window
   re-apply (which must stay benign), and a same-second `checkout` +
   `subscription.created` pair must both land. Residual exposure shrinks from ~3 days
   to a 1-second tie window — the pre-existing natural-jitter exposure.

2. **Identity backfill on stale skip.** A skipped stale `checkout.session.completed`
   still backfills `StripeCustomerId` / `StripeSubscriptionId` **when currently null**
   (never overwriting — a row already linked to a newer subscription wins). Checkout is
   the only event carrying the org→subscription link (`client_reference_id`); dropping
   it wholesale would leave the row unlinked, every later `customer.subscription.*`
   event would no-op (lookup is by `StripeSubscriptionId`), and billing-portal session
   creation would break on a null `StripeCustomerId`.

3. **Checkout entitlements derive from the live subscription.** The checkout handler
   already fetched the subscription from Stripe for the price id; it now uses the same
   response's `status` and `current_period_end` too. If the live subscription is
   terminal (`canceled` / `incomplete_expired`), the handler records identity and lands
   on the same free-tier state the deleted-handler writes, instead of granting paid —
   and stamps the fence at the *live subscription's* `EndedAt`/`CanceledAt` (not the
   checkout's own possibly-days-old `created`), so pending retries of pre-cancel events
   (`subscription.created/updated` "active", created after the checkout but before the
   cancel) read as stale rather than resurrecting active/paid through the side door.
   The fence is therefore "the as-of moment of the newest applied state", which the
   event `created` equals in every other path.
   This closes the sequence the fence *cannot see*: when the original checkout delivery
   failed, the row was never linked, so `customer.subscription.deleted` no-oped and no
   fence exists when the stale checkout retry arrives. Only live truth stops that
   resurrection. When no live state is available (no subscription id on the session, or
   Stripe unconfigured), the handler falls back to the historical "completed ⇒ active"
   grant.

   This clause refines ADR 0020's "re-applying the same event yields the same row
   state" wording for the checkout handler: a re-apply converges on live truth *at
   handling time* (which only ever moves toward terminal for a given subscription id).
   It remains a pure state-upsert — never increments, appends, or one-shot effects.

The fetch helper also fixes a latent bug found here: it now sets the Stripe API key
itself (via the `ClientOverride` seam, mirroring `CancelSubscriptionAsync`) —
previously the webhook path relied on some *other* code path having set the global
`StripeConfiguration.ApiKey`, so a fresh process whose first Stripe operation was a
checkout webhook 5xx-looped on "No API key provided".

## Consequences

### Positive
- A stale retried event (up to Stripe's ~3-day retry horizon) can no longer overwrite
  newer subscription state — the #275 resurrection sequences are closed end-to-end.
- The checkout handler is now self-sufficient (identity + plan + status + period end
  from live truth), so a skipped stale `subscription.created` loses nothing.
- Webhook handlers remain payload-driven and deterministic for
  `customer.subscription.*` events — the existing test harness keeps working.

### Negative
- Same-second out-of-order events can still apply in arrival order (accepted: that is
  the pre-existing jitter window, and strictly-newer-only ties would break ADR 0020's
  re-apply contract and drop same-second checkout/created pairs).
- The fence is read-check-write with no row lock or concurrency token, so *concurrent*
  delivery of two different events for the same row can bypass it within the handler's
  read-to-commit span (~ms): both read the old fence, the staler event commits last,
  state and fence regress — and nothing retries, since both events get recorded. This
  race predates #275 (previously the entire window was exposed, not just concurrent
  interleavings), and the residual guarantee is honestly "tie-second OR concurrent
  interleave", not the 1-second window alone. Accepted at current webhook volume: a
  `SELECT … FOR UPDATE` row lock (the `ExtractionWorker` precedent) is the escalation
  path if volume grows, but for the checkout handler it would hold a transaction open
  across the outbound Stripe fetch — the exact thing ADR 0020's Option A was rejected
  for — so it would need a fetch-before-lock restructure, not a drop-in.
- The fence is per-`Subscription` row, not per event type: an applied newer event of
  *any* mutating type fences out stale events of *every* type. This is deliberate —
  all four event types converge on the same row state — but a future event type whose
  handler writes disjoint fields must revisit clause 1. One known cross-*subscription-id*
  residual of the per-row fence: if an org ends up with two Stripe subscriptions during a
  webhook outage (double checkout — itself an anomaly requiring a Stripe-dashboard
  cancel of the duplicate), the terminal `EndedAt` fence from the canceled sibling's
  checkout retry can fence out the live sibling's earlier-created checkout retry,
  leaving the live subscription unlinked. Accepted: the row lands on `canceled`, which
  the 409 already-subscribed guard permits to re-checkout, and the duplicate already
  required manual intervention; per-subscription-id fencing was rejected above.
- `checkout.session.completed` handling now *depends* on the live fetch for terminal
  detection; a Stripe API outage during the fetch fails the handler (5xx → retry),
  which is the at-least-once recovery path working as designed (ADR 0020), not a new
  failure mode.

### Neutral
- `LastStripeEventAt` is null for every pre-#275 row and any org that never received a
  webhook; the fence is open until the first applied event stamps it.
- Events the handlers ignore (`invoice.payment_failed`, unknown types) never move the
  fence — it tracks the newest *applied* state, not the newest *seen* event.

## Alternatives considered

### Full live re-fetch in every handler (Option 2 alone)
Stripe's recommended pattern, and the most ordering-robust. Rejected as the blanket
mechanism: it adds an outbound call + failure mode to every event, and it would
invalidate the entire payload-driven webhook test suite (every
`customer.subscription.*` test drives state through payload snapshots; re-fetch would
require an HTTP-level Stripe stub threaded through the WebApplicationFactory webhook
path). ADR 0020 had already rejected (Option A there) coupling the webhook path to
implementation details for similar reasons. The one handler where the outbound call
already existed adopts the live-truth mechanism fully.

### Strictly-newer-only fence (skip ties)
Rejected: breaks ADR 0020's crash-window re-apply (same event, same `created`,
re-delivered after a success whose dedupe insert was lost) and drops the second event
of a same-second `checkout.session.completed` + `customer.subscription.created` pair —
the latter is the only carrier of `CurrentPeriodEnd` in that pair before this ADR.

### Per-event-type fence columns
One fence per event type would let a stale `updated` apply after a newer `deleted` of
a *different* type window — exactly the resurrection this ADR exists to prevent.
Rejected; all mutating types write the same row state, so one fence is correct.

## References

- Tickets: #275 (this decision), #268 (at-least-once), #48 (rolling epic)
- ADRs: 0020 (at-least-once idempotent handlers — extended, not superseded)
- Code: `StripeService.IsStaleEvent`, `ApplyCheckoutCompletedAsync`,
  `FetchSubscriptionAsync`, migration `SubscriptionLastStripeEventAt`
- Tests: `StripeWebhookTests` ordering suite, `StripeServiceCheckoutLiveStateTests`,
  `StripeEventFenceTests`
