# 0054. The public vendor portal gives notice at collection — on the surface, before the upload, in every state (CLM-5)

- **Status:** accepted
- **Date:** 2026-08-07
- **Deciders:** Ruben G. (founder), Claude (implementing #404)

## Context

`/portal/{token}` is the only surface in the product that a **non-customer** touches. The vendor did
not sign up, never saw a checkout, agreed to no terms, and in most cases has never heard of
CompliDrop — they got a link from a customer who wants a certificate. Until this change the page
told them two things: **"Secure upload"** and **"Powered by CompliDrop."** No privacy link, no terms
link, no disclosure of any kind.

What actually happens when they drop a file, each verified against code rather than against the
ticket's summary:

| Claim | Where it is true |
|---|---|
| The file is stored in Azure Blob | `Endpoints/VendorPortalEndpoints.cs` calls `IBlobStorageService.UploadAsync`; the implementation is the Azure `BlobContainerClient` in `Services/BlobStorageService.cs` |
| The file is read by third-party AI | `BackgroundServices/ExtractionWorker.cs` resolves `IOcrService` (Google Document AI, `Services/Ocr/DocumentAiOcrService.cs`) and then the LLM chosen by `Extraction:Provider` (`Services/Extraction/ExtractionClientFactory.cs`) |
| The visit is measured by PostHog | `app/layout.tsx` wraps **every** route in `Providers`, which calls `initAnalytics()` (`lib/analytics.ts`, `capture_pageview: true`). There is no `app/portal/layout.tsx`, so the portal inherits it |

All three are true. The third is worth stating precisely: the portal is not tracked by a portal-specific
integration that someone could remove — it is tracked because it is a route in the app, and *nothing
opts it out*.

The Privacy Policy already contemplates a version of this reader — *"If your information appears
inside a document that one of our customers uploaded…"* — but that paragraph is about the person
**named on** a certificate, not the person who **uploads** one. And in either case the vendor never
saw the policy, because nothing on the portal linked to it.

The legal hook is **CCPA notice-at-collection**: California's B2B exemption expired, so a California
vendor's personal information is covered, and notice-at-collection means the disclosure is given *at
or before* the point of collection. Beyond the statute this is plain fairness on the surface with the
least context and the least consent.

This is **counsel-gate item CLM-5** (`docs/rule-engine/G1-COUNSEL-BRIEF.md` §0, pending a licensed
attorney's sign-off), filed as [#404](https://github.com/neboxdev/complidrop/issues/404).

## Decision

**The portal discloses what happens to the document on the page itself, before the upload, and in
every state the route can render.**

### 1. AT collection means beside the dropzone, not after the upload

`UploadPrivacyNotice` renders directly beneath the dropzone and **above** the error and Received
cards. The load-bearing property is the word *at*: a disclosure that appears only on the success card
is one the vendor reads after we already hold their file, which is exactly the gap #404 reports,
relocated. The pin is shaped to that property rather than to the string — the test asserts the notice
is present in the state where the dropzone is live and `Received` is **absent**.

It is deliberately **not** conditioned on `atQuota`. The notice belongs to the collection *surface*,
not to a particular attempt, and a page whose dropzone is disabled is still the page the vendor is
reading.

### 2. Every state the route renders, including the two with no dropzone

The route has five render branches. Each was checked, not assumed:

| State | Disclosure |
|---|---|
| Loading shell (`PortalLoadingSkeleton`) | Full notice — as real copy, not a skeleton bar |
| Upload form | Full notice |
| At-limit link (`atQuota`) | Full notice (same return) |
| After a successful upload | Full notice (same return, above the Received card) |
| Dead link (404/410) / transient failure | `VisitPrivacyNotice` — the policy link plus the cookie line |

The loading shell carries the **real** sentence rather than a placeholder bar: the notice is the one
element that must never be pending, and rendering it there also stops the shell reflowing on settle.

The two terminal states get a different sentence on purpose. *"By uploading, you agree…"* is false
where there is nothing to upload — but the PostHog pageview fires on those branches too, so they
carry the visit disclosure and the same link. On the transient branch the notice sits **outside**
`role="alert"`: it is standing disclosure, not part of the failure a screen reader is being
interrupted for.

### 3. It names no AI vendor — the Privacy Policy owns that list

The portal sentence says *"automated reading by the AI services we use"* and links out. It does not
say "Google".

`Extraction:Provider` is a config switch (`gemini` | `anthropic`, `ExtractionClientFactory`), so a
provider name hard-coded into portal copy would go stale **silently** the day the switch moves —
copy on the highest-consequence surface, kept correct by nobody. The named subprocessor list lives in
one maintained place, `/privacy` § *Service providers we share data with*, which the notice links to.

Whether that list stays complete across every configured path is a **separate** counsel item —
CLM-6 / [#405](https://github.com/neboxdev/complidrop/issues/405) records that the Anthropic path is
config-reachable and Anthropic is not a disclosed subprocessor. #404 does not close it and must not
appear to: a portal sentence naming Google would make the disclosure *look* more specific while being
wrong on exactly the path CLM-6 is about.

### 4. The policy it links to now covers the person reading it

A notice pointing at a policy that does not address its reader is not notice. `/privacy` gained
**"If you were sent an upload link"**: no account is needed, what is collected (the file, the
technical data already described, the analytics cookie already described), that the file is stored
and read automatically by the service providers listed above, and that the business that sent the
link controls the record and is the faster route for a request about it.

It sits beside — not instead of — the existing *"If your information appears inside a document…"*
paragraph. Those are two different readers with two different remedies, and the test pins that both
survive. It re-names **no** subprocessor: the list above stays the single place they are enumerated,
which is also what keeps `marketing-content.test.tsx`'s per-vendor `getByText` pins single-valued.

### 5. On by default, not flag-staged — the ADR 0047 precedent, not the ADR 0043 one

Same reasoning as [ADR 0047](0047-exports-carry-a-non-advice-disclaimer.md) §4, and it is worth
restating because the repo has three default-OFF counsel-gated flags that look like the pattern to
copy:

- Those flags stage strings that change **what a verdict asserts** (ADR 0043's additional-insured
  wording). Flipping one alters legal meaning in either direction, so OFF is the safe default.
- Notice where there was none is **one-directional**. It cannot make the reader worse informed than
  "Secure upload" told them. A default-OFF flag would ship the code and leave the reported gap live.
- It changes **no verdict, no value and no API behaviour**. `/api/portal/*` is untouched.

The **wording** is provisional and CLM-5 stays ⬜: counsel confirms or refines the two sentences, which
is an edit to two components in one file.

## Consequences

### Positive

- The vendor is told, before they act, that the file is stored, that it is read automatically, and
  that the page uses analytics cookies — with the named-subprocessor list one click away.
- The policy the notice links to now has a section written for someone with no account.
- Frontend-only. No portal endpoint changed, no token added to any link or analytics property, no
  session required to read the policy.

### Negative

- Two more lines of fine print on a surface whose whole design goal is "drop the file and go". Sized
  down to two short sentences for that reason, and placed below the dropzone so it never sits between
  the vendor and the affordance.
- The cookie sentence is static, while PostHog is gated on `NEXT_PUBLIC_POSTHOG_KEY` being present
  (`lib/analytics.ts`). In an environment with no key the page says it uses analytics cookies while
  setting none. Accepted: over-disclosure carries no risk, and a notice that changes with a build-time
  env var is a notice nobody can rely on.
- The wording ships ahead of CLM-5 sign-off — mitigated exactly as ADR 0047 §5 mitigates CLM-3's:
  one-directional, and a reword is a one-file edit.

### Neutral

- `/terms` is deliberately not linked. The vendor is not entering a subscription agreement, and a
  second legal link on a favour-doing stranger's upload page trades disclosure for noise. The notice
  points at the document that describes what happens to their data, which is the question they have.
- The link opens in the same tab, matching every other legal link in the app (there is no
  `target="_blank"` anywhere in `frontend/`). The cost is a vendor who reads the policy *after*
  uploading loses the Received card on the way back; the card itself says the page can be closed, and
  the notice is positioned to be read before the upload, not after.
- Document-level analytics behaviour is unchanged — this ADR discloses PostHog, it does not gate it.
  A consent gate for the portal route would be a different decision with a different reader (GDPR),
  and CompliDrop processes primarily in the United States.

## Alternatives considered

### Option A — Put the notice only on the success card
**Rejected**: that is notice *after* collection. It is also where a reader is least likely to act on
it, since the thing they might object to has already happened.

### Option B — Name Google (and Vertex Gemini) in the portal copy, as the ticket's summary does
**Rejected**: see §3. Document AI is Google today and the default LLM is Gemini, but
`Extraction:Provider` is a config switch and the portal is the worst place in the product for copy
that silently stops being true. The named list belongs in the policy, and its completeness is CLM-6's
question, not this one's.

### Option C — A consent checkbox ("I agree") gating the dropzone
**Rejected**: notice-at-collection requires notice, not consent, and CCPA has no opt-in for this.
A checkbox adds a step for a stranger doing a customer a favour, converts a fairness fix into a
friction cost on the product's most abandonment-sensitive surface, and creates a consent record we
would then have to store and honour — new state on a public, unauthenticated endpoint.

### Option D — A cookie/consent banner on the portal route
**Rejected / out of scope**: a banner is a different regime (GDPR-style opt-in) and would be the only
banner in the app. If one is ever warranted it belongs site-wide, driven by a jurisdiction decision,
not bolted onto one route by a notice ticket.

### Option E — Link `/terms` alongside `/privacy`
**Rejected**: see § Consequences → Neutral. The Terms bind a *customer*; the vendor is not one.

### Option F — Suppress the cookie sentence when `NEXT_PUBLIC_POSTHOG_KEY` is unset
**Rejected**: it would make the disclosure depend on a build-time variable, so no two deployments
would necessarily say the same thing, and the honest failure direction here is over-disclosure.

## References

- Tickets: [#404](https://github.com/neboxdev/complidrop/issues/404) (bug, careful-review), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic); adjacent [#405](https://github.com/neboxdev/complidrop/issues/405) (CLM-6 — the provider-path disclosure this deliberately does not pre-empt)
- Gate: `docs/rule-engine/G1-COUNSEL-BRIEF.md` §0 (CLM-5) + §C
- ADRs: [0047](0047-exports-carry-a-non-advice-disclaimer.md) (the on-by-default disclosure precedent this follows), [0043](0043-additional-insured-claim-wording-staged-behind-flag.md) (the flag-staging precedent it deliberately does not follow), [0032](0032-portal-upload-idempotency.md) (the portal upload path being disclosed)
- Code: `frontend/src/app/portal/[token]/page.tsx` (`UploadPrivacyNotice`, `VisitPrivacyNotice`, `PrivacyPolicyLink`), `frontend/src/app/privacy/page.tsx` ("If you were sent an upload link")
- Tests: `frontend/src/app/portal/[token]/page.test.tsx` ("notice at collection (#404)"), `frontend/src/app/marketing-content.test.tsx`
