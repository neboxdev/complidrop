# Zero-touch sweep — no step may require talking to a human

**Ticket:** #240 (epic #234, track 2 — zero-touch onboarding)
**Date:** 2026-06-21
**Method:** code-level certification sweep across four subsystems (transactional-email honesty, error/limit-state copy, account-lifecycle + billing self-serve, email-link origins) plus a marketing/portal/support copy pass. Each customer-journey step is verdicted **self-serve OK** / **fixed here** / **ticketed**. Runs after #238 (sample demo) and #239 (guided onboarding v2), so it certifies the finished state.

> **Founder bar:** the app is ready to use by anyone, from the start, alone. A support contact may EXIST (#195) but must never be the ONLY path forward.

## Headline

**PASS.** No step in discovering, starting, using, or paying for CompliDrop requires human contact. The prior cold-start audit's loud defects are all closed (the audit hit them during the #247 outage window): resend-verification honesty (#249) and change-email honesty (#302) are fixed; every email-borne link mints from `Frontend:BaseUrl` and a boot-time validator refuses to start prod with a localhost/loopback/wildcard origin (#250, #301); upload-failure copy is friendly on both personas (#248). **One copy gap was found and fixed in this ticket:** the vendor portal went quiet after a successful upload. **No structural defects** were found, so no new tickets were filed.

## Sweep table

### Discover (marketing)

| Step | Verdict | Evidence |
|---|---|---|
| Landing hero CTA | self-serve OK | "Get started free" → `/register`; "No demo, no contract." (`app/page.tsx`) |
| Pricing | self-serve OK | Free/Pro/Annual each link to self-serve checkout; "No annual contracts. No minimums. No sales calls." — no "contact sales" / "book a demo" anywhere |
| Nav / footer "Support" | self-serve OK | Links to `/contact` (a `mailto:` resource), presented as optional help, never a required step |

### Start (account lifecycle)

| Step | Verdict | Evidence |
|---|---|---|
| Sign up | self-serve OK | `POST /api/auth/register`; no email-confirm-before-entry gate (soft-gate) |
| Email verify | self-serve OK | `/verify-email?token=…`; idempotent double-click → "already confirmed"; **expired link** → "Request a new one from your dashboard" (graceful, no dead-end) |
| Resend verification | self-serve OK + honest | `resend-verification` returns **503** when email is unconfigured and **502** when the provider rejects — never a false "sent." (#249 fixed; pinned by `EmailVerificationTests`) |
| Forgot / reset password | self-serve OK | Neutral "if that email is registered, we've sent a link" (anti-enumeration, by design); **expired reset link** → "Request a new one"; a successful reset is also the lockout escape hatch |
| Change password / email | self-serve OK + honest | Inline in Settings; change-email confirms on the NEW address; honest 503/502 before minting a token (#302 fixed) |
| Account lockout | self-serve OK | After repeated failures: "your account is locked for about N more minutes. Reset your password to regain access now." (relative time + recovery path) |
| Delete account | self-serve OK | Settings → danger zone; cancels the live Stripe subscription first, with an honest "we couldn't cancel… cancel from Manage billing first" fallback if Stripe is unreachable |

### Use (documents, vendors, portal)

| Step | Verdict | Evidence |
|---|---|---|
| Collect a document (owner) | self-serve OK | Upload / send a link / **try a sample** (#238); the checklist shows "link sent — waiting" (#239) so the funnel never goes quiet |
| Upload rejected (type / size / unreadable) | self-serve OK | `rejectionCopy` says what to do ("upload a PDF or a photo…", "take it again from further back…") — owner AND portal share it |
| Storage outage | self-serve OK | Friendly 503 "we couldn't store your file just now. Please try again in a few minutes." (owner + portal, #248) |
| Extraction failed | self-serve OK | Per-cause copy ("try a clearer copy" / "press Read again next month" / "try again") + optional Contact-support link |
| Plan document limit | self-serve OK | "Document limit of N reached. Upgrade to add more." → billing |
| Vendor portal — cold open | self-serve OK | "Hi {vendor} / {org} asked for your latest compliance documents"; owner's instructions rendered; "no account or password needed" |
| Vendor portal — rate limit / quota | self-serve OK | "Try again in about an hour" + retry button; quota → "ask your customer for a fresh link"; org-full → "let them know, they can make room" |
| **Vendor portal — after a successful upload** | **fixed here** | Was "Received · Processing…" then silence. Added a "what happens next": "That's everything {org} needs. They'll review your documents and reach out only if something's missing — you can close this page." (`app/portal/[token]/page.tsx`) |

### Pay (billing)

| Step | Verdict | Evidence |
|---|---|---|
| Trial → paid checkout | self-serve OK | `POST /api/billing/checkout` → Stripe Checkout; return `/settings?upgraded=true` shows a success toast |
| Declined card | self-serve OK | Handled inside Stripe's hosted checkout (the user retries the card there — zero-touch); a backed-out checkout returns `?canceled=true` → "Checkout canceled — no changes made." |
| Card update / cancel / invoices | self-serve OK | `POST /api/billing/portal` → Stripe billing portal (self-serve card change, cancel, invoice download) — no support email |

### Transactional email honesty (cross-cutting)

Every trigger follows the reference pattern (the portal-invite endpoint's honest "Email isn't set up yet, so we couldn't send it. Copy the link instead."): **no 2xx "sent" success unless `IEmailService` accepted the send.**

| Trigger | Verdict |
|---|---|
| Verify (register) | honest — best-effort soft-gate; registration never blocks on email, banner offers resend |
| Resend verify | honest — 503 unconfigured / 502 rejected / 200 only on accept (#249 fixed) |
| Password reset | honest-neutral by design (anti-enumeration) |
| Change-email confirm | honest — 503/502 before minting (#302 fixed) |
| Reminder (worker) | honest — records `Sent` vs `Failed`; a failed row retries (ADR 0025) |
| Portal invite | honest — the reference pattern |

### Email-borne link origins (cross-cutting)

All minted from `FrontendSettings.BaseUrl` — never localhost, never the request host: verify, reset, change-email confirm, portal link (reminder emails mint no link; they ask the user to log in). `FrontendSettingsValidator` **fails boot** outside Development if `Frontend:BaseUrl` is unset or resolves to a loopback/wildcard host (`localhost`, `127.0.0.1`, `0.0.0.0`, `[::]`, trailing-dot FQDN forms) — the exact class of bug that shipped localhost links in the audit (#250/#301).

## Fixes landed in this ticket

1. **Vendor portal "what happens next" after upload** (`frontend/src/app/portal/[token]/page.tsx`) — a cold vendor who's never heard of CompliDrop saw "Received · Processing…" then nothing, and could reasonably wonder whether it worked or whether more was needed (a silent generator of "did it go through?" emails to the venue). Now the Received card closes the loop: their documents will be reviewed, they'll only be contacted if something's missing, and they can close the page.

## Defects filed

None. The sweep found no structural defect — the four loud ones the #237 audit hit (#247/#248/#249/#250, plus the later #301/#302) were already closed by prior tickets, and verified still-closed here.

## Non-goals (held)

- The support channel stays (#195) — verified it's optional help, never the only path.
- No live chat / helpdesk tooling.
