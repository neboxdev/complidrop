# 0024. Paid entitlements gate on Subscription flags; portal lapse is neutral and reversible

- **Status:** accepted
- **Date:** 2026-06-12
- **Deciders:** Ruben G., Claude

## Context

The free plan's two fences were stored but never enforced (#261, found by the #236 audit):
`Subscription.HasVendorPortal` (false on free, true on checkout, false again on cancel) was
read by nothing, and the public portal upload checked only the per-link `MaxUploads` quota —
never the org's `DocumentLimit`, which the dashboard upload path enforces. A live Free org
generated and used a working portal link while its Settings tile read "Vendor portal: Off"
and the landing page sold the portal as Pro-only. This is the monetization fence the $49/mo
pricing model depends on.

Enforcing raised four product decisions (founder calls, recorded on #261, 2026-06-12):
gate or include; what happens to already-minted links when a plan lapses; what a vendor
sees when uploading into a capped-out org; and what happens to the founder's Free demo org.

## Decision

The portal is Pro-only, and enforcement follows four rules:

1. **Gate on entitlement flags, never the plan string.** Every paid-feature check reads
   `Subscription.HasVendorPortal` / `Subscription.DocumentLimit` — not `Plan == "pro"`.
   `StripeService` remains the SOLE plan→flag writer (checkout: portal on, cap null;
   subscription deleted: portal off, cap 5). A manual comp — flipping the flag directly,
   as done for the demo org — therefore grants the feature without a Stripe subscription,
   and a future capped tier is a data change, not a code change. Fail-closed: a missing
   Subscription row denies (every org gets one at registration; a missing row is corrupt
   state, not a free pass through a pricing fence).

2. **A lapsed plan kills existing links neutrally and reversibly.** The public portal
   (`PortalInfo` + `UploadViaPortal`) answers the byte-identical revoked-link message
   ("This upload link is no longer active.", 404) when the org's flag is off — a vendor
   must never learn the business's billing status. The gate is evaluated at request time
   and never mutates link rows, so re-subscribing flips the flag back and the same tokens
   revive untouched. Ordered before the expiry check (like the #269 dead-tenant guard) so
   a lapsed org's expired link doesn't 410-acknowledge a once-valid token.

3. **The org-side and vendor-side faces differ.** Link generation/emailing
   (`GeneratePortalLink` / `EmailPortalLink`) answer the org with a friendly upgrade 403
   (`plan.portal_not_included`), placed AFTER the tenant-filtered vendor lookup so
   cross-org probes keep their identical 404 (no existence oracle), and BEFORE the
   actionable 400s (no-contact-email, link-inactive) so a lapsed caller is never
   instructed toward a feature their plan lacks. The portal-side document cap
   (`vendor.portal_document_limit_reached`, 403) names the org and tells the vendor to
   contact them — never "upgrade", which the vendor cannot do. Mid-link cap-outs reject;
   accept-and-hold was explicitly rejected pre-launch (new document state, billing
   semantics, notification flow — a feature, not a fix).

4. **`GET /api/portal/{token}/status/{uploadId}` stays ungated** (like its existing
   `IsActive` carve-out): the fence stops new intake, not status reads — a vendor who
   uploaded minutes before the lapse must still see "we got it".

The portal `DocumentLimit` check mirrors the dashboard's read-then-insert semantics
(count active docs, `DeletedAt == null`, best-effort) — deliberately not atomic; a
concurrent pair landing one document over a 5-doc fence is acceptable, and both ingress
paths stay consistent.

## Consequences

- The Settings tile / landing copy halves (FP-113, FP-011) implement against this
  decision in #241; the onboarding-checklist hint went plan-aware in #261 itself.
- The demo org (The Garden Hall) is comped by flag flip in prod — plan stays "free",
  tile reads "On". Any future comp follows the same shape.
- Tests pin the contract in `FreePlanFenceTests` (both gate faces, neutral-message
  parity by live comparison, revival, fail-closed missing row, cap at/under/soft-deleted,
  status carve-out, and an end-to-end signed-webhook → dead-portal chain).
- A future plan that includes the portal WITH a document cap works out of the box:
  the two flags are checked independently (pinned by test).
