# Onboarding cold-start audit — signup to first verdict as Pat

**Ticket:** #237 (epic #234, Track 2 — zero-touch onboarding)
**Date:** 2026-06-10, ~10:45–11:30 CET
**Environment:** production (`www.complidrop.com` / `api.complidrop.com`), fresh throwaway org
**Persona:** Pat Rivera, 50-something office manager of "The Garden Hall" (event venue, 1–9 people), non-technical, no compliance training, signs up cold with no human contact. Email `rubengg2016+pat-0610@gmail.com` (real, monitored inbox — deliverability was part of the test).
**Method:** drove a real Chrome session through the exact funnel: marketing → signup → (email verify) → onboarding → add vendor → define requirements → get a document in (owner upload AND vendor portal) → first verdict. Per step: what Pat sees, what she'd click, where she stalls, what jargon assumes knowledge, what data she lacks, where the app goes quiet. The vendor side was walked cold as well.

> **⚠️ Walk truncated by a prod outage.** Document upload (both paths) is 500-broken and no transactional email sends on prod — three missing/broken env settings, filed as **#247** (P0, with diagnosis). The funnel is therefore measured **signup → "document in"**, and the **extraction-wait → first-verdict leg is untested**. §6 lists exactly what to re-measure once #247 closes. This is itself the audit's loudest finding: *as of today, no cold signup can reach a verdict at all.*

---

## 1. Step-by-step trace

Wall-clock times are automation-paced (a floor, not a human pace); the "Pat pace" column is an estimated realistic human duration including reading time.

| # | Step | Wall clock | Pat pace (est.) | Verdict |
|---|------|-----------|------------------|---------|
| 1 | Marketing landing | 10:45:43 | 30–60 s | ✅ strong |
| 2 | Register form | +30 s | ~90 s | ✅ strong |
| 3 | Land in app + welcome modal (3 slides) | 10:46:44 | ~60 s | ✅ strong |
| 4 | Add first vendor | 10:47:58 | ~60 s | ✅ strong |
| 5 | Assign "what they must prove" | ~+2 min | 1–3 min | ⚠️ works, two stalls |
| 6 | Upload COI (owner path) | 10:57 | — | ❌ **blocked (outage)** |
| 7 | Portal link → vendor side (cold) | ~11:15 | — | ⚠️ good copy, ❌ upload blocked, ❌ link URL broken |
| 8 | Extraction wait → first verdict | — | — | ⛔ **unreachable, untested** |

### Step 1 — Marketing landing ✅
Headline "Stop chasing certificates of insurance. Start dropping docs."; sub-copy expands COI in plain words and states **"$49/month. No demo, no contract."** CTA **Get started free** with "Free for your first 5 documents. No credit card." Zero-touch posture is excellent — nothing funnels toward a human. Pat clicks the orange button without hesitation.

### Step 2 — Register ✅
Four required fields (name, company, email, password) + optional free-text Industry/Size. Password requirements shown up front and tick live. `+`-alias email accepted. No CAPTCHA, no email-confirm-before-entry. Fast and friction-free.

### Step 3 — Welcome ✅
Lands **directly on the dashboard** (no verification gate — verification is a polite banner: "Confirm your email … so your compliance reminders actually reach you" + Resend). Welcome modal: slide 2 ("Four steps to covered": add a vendor → set what they must prove → collect the document → read the result) teaches the entire mental model in plain English; slide 3's CTA deep-links to Vendors. Behind it, a **state-aware "Get started" checklist** (4 steps, "Expiry reminders are on" honestly pre-ticked). This is the right shape.

### Step 4 — Add first vendor ✅
Vendors page has an inline two-field add form (name + contact email), a teaching tip banner, and an explanatory empty state. After add, the row itself shows the next action: **"Set requirements"**. No dead end.

### Step 5 — "What this vendor must prove" ⚠️
Vendor detail offers a dropdown of suggested checklists with the helper "Pick the checklist for their type — we check every document against it", and an honest warning while none is set. Two real stalls:

1. **Every checklist appears twice** (Caterer ×2, Security Service ×2, …) — the system template set is duplicated in the prod DB (seed/rename race; **#251**). Pat cannot know which "Caterer" is right.
2. **Nothing shows what the chosen checklist demands.** After picking "Caterer", neither the vendor page nor anywhere on the path says "this checks: ≥ $1M general liability, expiration date present, workers-comp coverage". Pat proceeds on faith. (Feeds #239 — see §4.)

A third, related seam appears later: the **Vendor requirements page says "YOUR CHECKLISTS — None yet"** even though Pat just assigned "Caterer" to her vendor (system templates assigned directly never become "hers"). "Where did my Caterer list go?" (Feeds #239/#238.)

Checklist state-awareness confirmed: after the assignment, the dashboard step "Choose what they must prove" ticks (on next page load).

### Step 6 — Owner upload ❌ outage
Documents page itself is well-shaped: teaching tip, dropzone with format/size, and a **pre-upload panel** (file chip + vendor picker that suggests Brightside + doc type preselected "Certificate of Insurance" + upload button disabled with the reason spelled out). Then the wall: **every upload returns a raw 500** — toast says *"An unexpected error occurred."* and nothing else. No next step, no retry hint, no support pointer. Pat is dead-ended at the exact moment of highest motivation. Root cause + repro in **#247** (Azure storage config); friendly-failure hardening in **#248**.

### Step 7 — Vendor side, cold ⚠️/❌
- Link generation works, but on prod the minted URL is **`http://localhost:3000/portal/<token>`** (**#250** / #247) — any link Pat copies or emails strands her vendor on a dead page.
- The invite email path fails *honestly*: "Email isn't set up yet, so we couldn't send it. Copy the link and send it to your vendor instead." — the right pattern, and the only place in the app that admits the email subsystem is down. The signup-verify Resend button **lies** ("Verification email sent.", 200) — **#249**.
- The portal page itself (opened with the token on the correct domain) reads well cold: "Secure upload / **Hi Brightside Catering Co.** / The Garden Hall asked for your latest compliance documents." + an instructions card + dropzone with quota ("0 / 20 uploads used on this link") + "Powered by CompliDrop". A vendor who has never heard of CompliDrop knows who's asking and what to do. Camera affordance verified at DOM level (`accept="image/*,application/pdf"`, mobile "Tap to…" copy present; #196 landed).
- Vendor upload → same 500, rendered as a bare inline *"An unexpected error occurred."* — for an external, phone-holding vendor this means "give up" or "call Pat". (#247/#248; vendor-side copy in §4 → #240.)

### Step 8 — Extraction wait → first verdict ⛔
Unreachable. The post-upload silence question ("does the app go quiet during extraction?"), extraction duration, verdict legibility, and the verdict's explanation quality are **all untested**. See §6.

### Side observations (cold-start-relevant)
- **Reminders**: zero-setup (7/14/30/60-day ladder pre-enabled, team+vendor toggles, "sent at 8 AM in your org's local time zone"), and Settings auto-captured the timezone with a superb helper ("It's 11:22 AM there now — reminders send at 8:00 AM, so the next one goes out tomorrow."). The checklist's pre-ticked "Expiry reminders are on" is honest.
- **Settings**: complete self-serve lifecycle — restart tour, change password, change email (confirmation-link pattern), JSON data export, delete account. Strong #240 posture. But the **"VENDOR PORTAL: Off"** plan tile contradicts the portal actually working on Free (**#253**).
- **Activity feed**: every explicitly-logged action appears **twice** ("Vendor added" ×2 …) and internal flag flips leak as "User · Updated" (**#252**). Pat-facing effect: "did I add the vendor twice?"
- Free plan = 5 documents, portal quota 20/link; no billing gate appears anywhere in the cold path (good).

---

## 2. Baseline metrics

| Metric | Value | Notes |
|---|---|---|
| Steps signup → document-in (owner path) | **9 screens / ~14 interactions** | marketing → register → modal ×3 → vendors → vendor detail → documents → upload panel |
| Wall clock signup → upload attempt | **11 min** (automation floor ~6 min of it) | includes the duplicate-template stall |
| **Estimated Pat pace, signup → document-in** | **~6–8 min** *if she has a COI file at hand* | reading-speed estimates per step |
| Estimated Pat pace, signup → first verdict | **~8–10 min + extraction time** — *unverifiable today* | extraction leg untested (#247); 10-min epic bar is in reach but not proven |
| Hard jargon stalls | **0** on the walked path | post-#188/#192 copy holds up; COI expanded at first use; "what they must prove" framing works. Caveat: requirement *contents* (limits/operators) were never shown to Pat at all — the jargon test for those is deferred to the requirements-detail flow (#239 must surface them in plain sentences) |
| Decision stalls | **2** | duplicate "Caterer" pick (#251); "which of the 2 collect paths" (upload vs link — mild, checklist copy handles it) |
| Dead ends | **3** | owner upload 500 (no action offered); vendor portal upload 500 (no action offered); verify-email loop (resend "succeeds", nothing arrives — invisible) |
| Quiet zones | **1 confirmed, 1 unknown** | confirmed: nothing tells Pat emails aren't going out; unknown: post-upload extraction wait (untested) |
| Data Pat lacks | **1 critical** | a COI file. Everything else (vendor name/email, venue type) she knows by heart. This is the entire case for #238 |

**Bar check (epic #234):** "cold signup reaches first verdict unaided in <10 min" — **cannot pass today** (outage). The shape of the funnel up to the wall suggests the bar is achievable once #247 closes *for a Pat who has a COI file*; for one who doesn't, the only path is portal-link + waiting on the vendor — hence #238's sample-document demo is what makes the "<2 min sample path" bar possible at all.

---

## 3. Ranked findings

**P0 — launch-blocking, filed as bugs**
1. #247 — prod env config missing: every upload 500s (both personas), no transactional email at all, portal links minted as `localhost`. *No cold signup can succeed until this closes.*
2. #251 — duplicated system templates (first decision of the funnel is a coin flip).
3. #249 — verify-resend claims success while email is down (invisible stranding; also blocks the entire "email verify" leg of the funnel).
4. #250 — localhost links would strand real vendors even with email fixed.

**P1 — funnel quality (route to implementation tickets, §4)**
5. Upload failure (any cause) is a copy dead end: raw "An unexpected error occurred." with no next action, dashboard + portal (#248 server side; #240 sweep for the copy).
6. Chosen checklist contents invisible at pick time (vendor page) and the /rules "None yet" seam (#239).
7. No no-document path: with no COI at hand the checklist's "Upload a COI" is a dead recommendation; the link path depends on vendor latency (#238).
8. Portal plan tile contradiction (#253 — semantic decision).
9. Activity feed duplicates + entity-speak (#252 — trust erosion on the very first session).

**P2 — polish noted for #236's full pass** (not expanded here): activity timestamps with seconds; export page could teach when empty; portal page `<title>` is the generic marketing title; date rendering follows browser locale (verify against Pat-in-Texas expectations).

**What already works — do not rebuild** (evidence for keeping #239 scoped): marketing zero-touch posture; no-gate signup; welcome modal teaching the 4-step model; state-aware checklist (MVP version is real, not click-to-dismiss); vendors inline add + row-level next action; pre-upload details panel; reminders zero-setup + timezone helper; settings lifecycle; portal cold-open copy + camera affordance.

---

## 4. Gap map → implementation tickets

### → #238 (instant value: sample certificate + starter templates)
- **Confirmed**: the COI file is the single missing asset for a fast first verdict; the sample-document demo is the only guaranteed-fast path and the only way to hit the epic's "<2 min sample path" bar. Entry points per the audit: the dashboard "Drop a document" card + the checklist's "Collect a document" step (its copy should offer "No document handy? Try a sample certificate").
- **Scope correction**: "starter requirement templates" already exist server-side and surface in two places (vendor-page dropdown + /rules suggested checklists with clone-on-"Use this"). #238 should NOT rebuild them; it should (a) depend on the dedupe fix #251, (b) decide the system-template-vs-org-clone model (the "/rules says None yet while a system template is assigned" seam — either clone-on-assign so the checklist becomes editable + visible as "yours", or render assigned system templates as first-class), and (c) focus net-new work on the sample COI pipeline.
- Sample COI fixture spec from this audit: obviously-fictional insurer/insured names + "SAMPLE — NOT A REAL CERTIFICATE" footer; GL ≥ $2M so it passes the Caterer template; the audit's generator (ACORD-25-style HTML→PDF) can be reused.
- Cost note stands (live pipeline per click); also ensure the sample works with email/storage *degraded* messaging in mind — the demo must fail loudly if infra is down, not "An unexpected error occurred."

### → #239 (guided onboarding v2: state-aware checklist + empty states)
- The MVP checklist is genuinely state-derived — v2 is an upgrade, not a rescue. Audit-grounded deltas, in priority order:
  1. **Show requirement contents at decision time**: one plain-English line per rule under the vendor-page dropdown once a checklist is chosen ("We'll check: at least $1,000,000 general liability · expiration date present · workers-comp coverage"), reusing #192's sentence rendering. This is the single highest-leverage guidance gap found.
  2. **Heal the /rules seam**: the requirements page must acknowledge system templates assigned to vendors ("Caterer — used by Brightside Catering Co. (suggested checklist)") instead of "None yet".
  3. **"Collect a document" step branches**: upload / send link / *try a sample* (ties to #238); the step should also reflect the link-already-sent state ("Link sent to Brightside — waiting for their upload") so the funnel doesn't go quiet while waiting on a vendor.
  4. Live tick without page navigation (aria-live per #189) — currently ticks on next load; acceptable but v2 should tick in place.
  5. Empty-state inventory addition from this walk: Export with zero documents (teach: "your audit report will appear here once the first document is in").
  6. **Post-upload quiet zone**: instrument the extraction wait (skeleton + "we're reading it, ~a minute" + auto-refresh) — flagged from architecture, unverified due to #247; re-check before building (§6).
- Welcome modal, tips, and dashboards need no rework — leave them.

### → #240 (zero-touch sweep)
Add these audit-confirmed items to the sweep checklist:
- Resend-verification honesty (#249) and, generally: **no success copy unless the provider accepted the send** — sweep every transactional email trigger.
- Upload failure copy (dashboard + portal) must say what to do next; the portal variant needs a vendor-appropriate fallback ("Couldn't upload? Reply to the email from The Garden Hall / contact them directly") because the vendor has no other recourse (#248 provides the server-side envelope).
- Email-verify banner behavior when the email subsystem is down — today it nags + offers a Resend that cannot work; the invisible-stranding case (#249) is exactly the sweep's "delivery-failure handling does not strand the user invisibly" line.
- Portal links must never leave the public origin (#250) — sweep ALL email-borne links (verify, reset, reminder, portal) post-fix.
- Portal plan-gate decision (#253) ripples into checklist + pricing copy — sweep after the founder call.
- **Deliverability leg is entirely untested** (no email left the building): post-#247, the sweep must verify verify/reset/reminder/portal-invite emails arrive, render, and link correctly — including spam-folder placement on a cold Gmail.
- CLAUDE.md secrets list now includes `Frontend:BaseUrl` (fixed in the #237 PR) — sweep that prod parity actually holds.

---

## 5. Notes on method & validity

- Automation pace ≠ human pace; human estimates are reading-speed reconstructions, marked as such. Two early UI mishaps (a "lost" template save, a no-op email-link click) were traced to the automation clicking stale element references after layout shifts — re-tested cleanly and **excluded** from findings.
- The browser session ran with a Spanish/Canary locale; date-format observations are locale-relative and deferred to #236.
- The org used (`The Garden Hall`, vendor `Brightside Catering Co.`) is throwaway; cleaned up at audit close per #228 hygiene (self-serve in-app delete — itself a #240 datapoint — plus a DB sweep of the soft-deleted remnants). The §6 re-run starts from a fresh org.

## 6. Re-run checklist (after #247 closes)

The blocked tail, to be walked on a fresh org before #239 implementation starts:

1. Verify email arrives (timing, spam placement, copy, link works) → completes the "email verify" leg.
2. Owner upload of a clean COI → measure: post-upload feedback, extraction wait duration + whether the UI goes quiet, auto-refresh behavior.
3. **First verdict**: time signup→verdict (the epic's <10 min bar), verdict legibility as Pat (does "Covered / not covered" + reasons read plainly?), and what the verdict screen offers as the next action.
4. Portal-link email arrival + vendor upload E2E on the corrected public URL (phone viewport).
5. Confirm the checklist's "Collect a document" ticks on the portal-upload path too (not just owner upload).
6. Re-measure the §2 table and update this doc in place (append a "Re-run" section; keep the original numbers for the before/after).
