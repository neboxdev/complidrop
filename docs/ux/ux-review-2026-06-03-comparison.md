# UX review reconciliation — prior 5-pass vs. fresh 18-agent (2026-06-03)

> **Update — a second fresh pass ran.** The fresh review now has two passes: the broad **pass 1** ([`-multiagent.md`](ux-review-2026-06-03-multiagent.md)) and a **gap pass** ([`-gap-pass.md`](ux-review-2026-06-03-gap-pass.md)) targeting mobile / accessibility / performance / forms / functional escape-hatches. The gap pass **confirmed every item in §2 below in code** and added **104 net-new findings (16 blockers)**. §4's roadmap reflects both passes.

Two independent UX reviews of CompliDrop now exist (the fresh one run in two passes), both dated 2026-06-03:

| | Prior review | Fresh review |
|---|---|---|
| **File** | [`ux-review-2026-06-03.md`](ux-review-2026-06-03.md) | [`ux-review-2026-06-03-multiagent.md`](ux-review-2026-06-03-multiagent.md) |
| **Method** | 5 sequential passes, one author, single context | 18 parallel agents (1 per surface/journey) + synthesis |
| **Strengths** | Mobile, a11y, **build-ready specs** (password-reset, onboarding) | Breadth (232 findings), billing/export defects, **implementation-grade Rules redesign** |
| **Persona** | Non-technical SMB staff, **often on a phone** | "Pat" — non-technical venue office manager (desktop-leaning) |
| **Verdict** | "core loop built like an internal admin tool" | **3/10 intuitiveness**; "Pat cannot complete the core job today" |

**Bottom line: the two passes strongly agree on every structural blocker** — which is the most valuable signal here. Two independent methods converging means these are real and worth acting on without further debate. They differ at the edges, and each caught things the other missed. This doc reconciles them into one plan.

---

## 1. Corroborated — both reviews found it (high confidence, act on these)

| Issue | Prior | Fresh |
|---|---|---|
| **Rules page is a raw DB grid** → rebuild as a plain-English sentence/checklist builder | P0 #3, §5 | #1 + full redesign |
| **Upload typed `OTHER` / no vendor** → doc never evaluated, "Pending" forever | P0 #2, §3 | #2 |
| **Zero onboarding / all-zeros first run** with no "start here" | P0 #4, §7 | #3 |
| **Pervasive jargon**; humanize enums (`ManualRequired`/`NonCompliant`/`ExpiringSoon`) | P0 #6, §4 | #4, theme 2/5 |
| **No "Forgot password?" + lockout dead-end** | P0 #1, §6 | #10 |
| **Reminder labels hide who gets the email**; humanize `bounced`/`complained` | §5 | #13, #14 |
| **Vendors:** "Template"→"Requirements", **"Email the link to {vendor}"**, label revoke X | §5 | #5, #9, #23 |
| **Confidence %** is anxiety-inducing → humanize / guide | §4 | #7 |
| **Doc detail:** rename "Re-extract", humanize `snake_case` field labels, guide low-confidence | §3 | #6, #17, #20 |
| **Native `confirm()`** → styled dialog naming the consequence | §3 | #25 |
| **Dashboard:** clickable stat cards; expiry-pipeline `max=10` scaling bug; rename chrome | §2 | #18 |
| **Portal `info.instructions` collected but never rendered** (real bug) | §5 | portal/major |
| **"LLM spend MTD"** remove/relabel | §5 | #24 |
| **Landing:** show value, build trust, add a contact path | P0 #7, §1 | #21 |

---

## 2. Gaps pass-1 missed — now CONFIRMED in code by the gap pass ✅

Pass-1 optimized for copy/IA/flow and **under-covered presentation-layer concerns**. The **gap pass (pass 2) has since confirmed every item below in the code, with file/line evidence, and expanded each** — see [`-gap-pass.md`](ux-review-2026-06-03-gap-pass.md). These are no longer "prior-only"; they are corroborated and detailed there:

- **🔴 Mobile / responsive (prior P0 #5) — fresh found ZERO.** Hardcoded `240px` sidebar with no hamburger/drawer (`(dashboard)/layout.tsx:42`); `documents`/`vendors` tables use `overflow-hidden` and clip columns on phones. Confirmed in the code. For a "scan from your phone" pitch this is a launch blocker. **Treat the prior mobile findings as authoritative.**
- **🔴 Reminder-toggle accessibility (prior P0 #8) — fresh found only 3 a11y items, missed this.** Bare `<button>` switches, no `aria-label`/`role="switch"`/`aria-checked`, state by color only — and they control whether compliance emails send.
- **📋 Password-reset build spec (prior §6).** Fresh only flags the gap; prior has a complete, house-pattern spec (token entity, hashing, endpoints, 45-min expiry, clear-lockout-on-reset, migration, frontend pages, bundle change-password). **Use the prior spec.**
- **📋 Onboarding design + persistence (prior §7).** Fresh proposes a 3-step "Get started" card; prior adds the full mechanism: welcome modal + data-driven checklist + coachmarks on **Base UI**, server flag `User.HasCompletedOnboarding` (vs localStorage), restart-tour link. **Use the prior design.**
- **Email verification gap (prior §6)** — reminders go to an unverified address, so a typo silently breaks the product. Fresh missed it.
- **Timezone not editable in Settings (prior §5)** — silently controls the "8 AM local" reminder timing; can't be fixed if mis-detected. Fresh missed it.
- **Session-expiry is invisible (prior §6)** → redirect to `/login?expired=1` with a message. Fresh missed it.
- **Documents list has no search/filter/sort (prior §3)** — needed once a venue has 50+ docs. Fresh missed it.
- **Landing legal footer — no Privacy/Terms/Contact (prior §1)** — a trust gap *and* a Stripe expectation. Fresh flagged contact/support but not the legal pages.

---

## 3. Fresh-only — real gaps the prior pass missed

Breadth and the backend read surfaced defects the prior pass didn't:

- **🔴 Raw Stripe `past_due` leaks into plan prose** ("…pro plan · past_due"). Alarming, no fix offered to the user. *(fresh #15)*
- **🟠 Annual card shows `$39/mo` but hides the `$468` charged today.** Prior noted "savings math unanchored"; fresh pins the actual trust-destroying surprise — and the label (`annualBilledLabel`) already exists in `plans.ts`, just unrendered. *(fresh #16)*
- **🟠 "Why is this not compliant?" — failed-rule `errorMessage`s are computed and stored but rendered nowhere.** A red badge with no reason and no next step. Big trust/utility gap. *(fresh #8)*
- **🟠 Export PDF prints raw user GUIDs and is silently capped at 500 events.** Prior called Export "good"; fresh found real audit-credibility defects. *(fresh #22)*
- **🟠 Activity feed garbles camelCase** → "Compliancetemplate · Created" (the `prettyAction` regex splits on `.`/`_` but not camelCase). Looks broken. *(fresh #11)*
- **The "not expired" honesty trap (verified in `ComplianceCheckService`).** The engine has no "date in the future" operator; a `required` rule on `expiration_date` only checks a date *exists* — expiry is auto-computed separately. The redesign's toggle copy is worded to stay truthful. This is a correctness insight that sharpens the shared Rules recommendation. *(fresh redesign)*
- **🟠 Reminders page has no empty/loading/error states** — a failed load looks identical to "all good." Prior focused on the toggle a11y. *(fresh #12)*
- **🟠 Processing-error card leaks the raw internal error string** ("extraction.too_many_attempts…"). *(fresh #19)*
- **Implementation-grade Rules redesign** — exact plain-language→`fieldName/operator/expectedValue` mapping, money formatter, clone-via-frontend-replay (no migration), before/after copy table, open questions. The prior "sentence-builder" sketch is correct but lighter; **use the fresh redesign as the build spec.**

---

## 4. Final merged roadmap — both passes, deduped & sequenced

Reconciles the prior §8 roadmap, fresh pass-1 priorities, and the gap pass. Each batch = its own ticket/PR. (P1 = pass-1 finding #; gap = gap-pass finding #.)

| Order | Batch | Contents | Source |
|---|---|---|---|
| **0** | **Make the app usable on a phone** (NEW — do first) | Responsive shell: drop the inline 240px style, add hamburger + drawer (Sheet); core tables → stacked cards / `overflow-x-auto`; responsive `grid` prefixes; 44px touch targets | gap #1,2,7,11 |
| **1** | **Stop locking people out & losing data** | Password reset + change-password (prior spec); **email verification** (gap #3); **editable timezone** + org-update endpoint (gap #5); don't evict a valid session on a transient `/me` (gap #9, *bug*); change-email / delete-account / data-export (gap #18) | prior §6 + gap |
| **2** | **Make the engine answer "is this vendor covered?"** | Vendor + doc-type at upload + editable type (P1 #2); vendor "no requirements" amber warning (P1 #5); documents **pagination + surface the existing filters** (gap #6) | P1 + gap |
| **3** | **Stupid-proof quick wins** (1 low-risk PR) | Enum→English map incl. export; field-label map; dashboard copy + dynamic chart max; activity-feed map; confidence-% → tiered; "Read again"; reminder labels; Stripe-status friendly + `$468/yr` label; "LLM spend" relabel; login "Forgot password?" link; unify "Requirements"; **email-the-link-to-vendor** | P1 |
| **4** | **Accessibility & inclusive hardening** | Reminder **switch** semantics + 44px (gap #4); `aria-describedby` + on-blur (gap #8); status **icons** (gap #12); contrast slate-400→500 (gap #13); skip link + nav label + `aria-current` (gap #14); icon-button aria-labels (gap #15); `aria-live` completion (gap #17); `prefers-reduced-motion` (gap #19); password show/hide (gap #20); `confirm()`→AlertDialog (gap #22 + P1 #25) | gap + prior P0 |
| **5** | **Onboarding MVP** | Welcome modal + data-driven checklist + `HasCompletedOnboarding` server flag (Base UI) | prior §7 |
| **6** | **Rules → "Vendor requirements" redesign** | Plain-English checklist builder (pass-1 redesign = build spec); fold the stacked mobile form + labeled controls (gap #21) | both |
| **7** | **Document-detail trust cluster** | "Why is this not compliant?" (surface failure notes); `ManualRequired` CTA; humanize processing-error card | P1 #8,19,20 |
| **8** | **Launch trust & billing readiness** | **Legal footer: Privacy / Terms / Contact** — a Stripe Checkout prerequisite, *gates charging cards* (gap #10); social proof + support link; de-jargon pricing; mobile marketing nav; portal **camera capture** + branded loading + render owner `instructions` (gap #16 + P1 portal bug); product screenshots; signup trim | both |
| **9** | **Polish & scale** | Export (GUIDs→names, truncation note, date-range scope, P1 #22); skeletons replacing "Loading…" text (CLS); abort-signal on polling; any remaining search/filter | both |

**Why this order:** Batches **0–2 are the structural launch-gates** — the app is unusable on the phones the pitch promises (0), people get locked out and data silently vanishes (1), and uploaded COIs never get evaluated (2). Copy fixes land on screens Pat can't open until Batch 0 ships, which is why mobile moved to the front. **If you are already charging cards, pull the legal footer (Batch 8 / gap #10) forward now** — Stripe Checkout requires accessible Terms/Privacy. Batches **3–4** are the highest leverage-per-hour and mostly low-risk (many gap items are one-token edits). **5–6** unlock the core value for non-technical users. 7–9 follow. Apply the `bug` label (epic #48) to gap #9 and the data-loss items.

---

## 5. Recommended next step

The two passes change the front of the queue: **mobile is now the gating item**, because every copy/flow fix lands on a screen Pat can't open until it ships.

Recommended parallel start:
1. **Batch 0 — mobile shell** (the responsive sidebar/drawer is the unlock; the rest are mostly one-token grid edits).
2. **Batch 3 — quick wins** (near-zero-risk humanization; immediate "built for me" jump).
3. **Batch 1 — access/account** via `/plan` (password reset has a ready spec; email-verification + timezone + the session-eviction *bug* carry real data-loss risk).

Then take the **Rules → "Vendor requirements" redesign (Batch 6)** through `/plan` — the #1 blocker, now with an implementation-grade design. **If cards are being charged today, do the legal footer (gap #10) immediately** — it's a Stripe prerequisite, not a nicety.

*Optional but recommended:* a **live click-through pass** (run the stack, drive each screen on a phone viewport) would visually validate the mobile/contrast/touch findings and catch rendering-level issues static code-reading can't.
