# CompliDrop — Final UX pass, post-overhaul (2026-06-10)

**Ticket:** #236 (epic #234). **Persona & rubric:** identical to the 2026-06-03 baseline — "Pat", a non-technical 50-something office manager at a Texas event venue (aging eyes, often on a phone); her vendor "Tony" is phone-only. Score = *intuitiveness for a non-technical SMB user, N/10*. Pre-overhaul baseline: **3/10 overall**. Epic exit bar: **every page ≥ 8/10**.

**Method.** (1) Live drive of the real assembled app — signup → onboarding → vendor → checklist → upload → real extraction (Document AI + Gemini) → verdict → portal link → vendor upload at 375×812 — with DB-level verification of every pipeline claim. (2) Eight parallel fresh-eyes agents reading ONLY product source (forbidden from docs/ux, docs/qa, .claude, and GitHub issues), one per surface: marketing+auth, dashboard+onboarding, documents, vendors+requirements, reminders/export/settings, portal, a11y+mobile, backend promise-vs-reality. (3) Targeted re-verification of every distrusted attempt-1 claim (this audit is the clean redo of the contaminated 2026-06-10 first attempt; its PR #267 was discarded).

**Environment notes — read before trusting any live observation in any future audit:**
- **The dev environment shares the PRODUCTION database and storage account.** The local user-secrets `ConnectionStrings:Database` points at the same Neon DB that Railway prod serves: the org table contains the founder's prod demo org ("The Garden Hall"), every #247 prod probe org, attempt 1's "Bluebonnet Event Hall", and now this audit's "Lone Star Pavilion". Consequences: Railway's always-on `ExtractionWorker` is a permanent second queue consumer racing any local API (this fully explains attempt 1's "unreclaimed zombie" observations); a local `dotnet run` can auto-migrate prod schema at boot (`Database:AutoMigrate` defaults on); local uploads burn prod extraction spend. Filed as #271.
- Because of the shared queue, the planned single-process zombie-reclaim repro is **impossible to run live in this environment** (demonstrated: a doc reset to Pending while the local API was *dead* was claimed within 2.1 s — by Railway). The #243 concurrency audit must use the integration-test route its ticket already prescribes.
- Two attempt-1 anomalies were reproduced and **ruled artifacts**: (a) the documents list "never updating" during extraction — the preview browser tab is `document.hidden`, so TanStack's `refetchInterval` legitimately pauses; a real foreground tab updates within 5 s; (b) the register form's pre-hydration native GET leaking the password into the URL — `/register` is **statically prerendered** (`next build`: `○`), so production ships the skeleton (no `<form>`) until hydration; the leak is reachable only in dev mode. Dropped (optional hardening: `method="post"` on the form, P3).
- Resend email delivery in dev was NOT re-tested via inbox (Gmail MCP reads the wrong account — known trap); deliverability was proven on prod by #237.

## Verdict

**Overall: 6/10** (baseline 3/10). The overhaul genuinely landed: jargon is gone from the happy path, onboarding teaches the loop, the mobile shell works, the portal reads warmly, and signup → first verdict took **~9 seconds of pipeline time** on the happy-path fixture. What holds the product at 6 is no longer copy — it is **trust mechanics**: verdicts that silently go stale (and contradict the dashboard on the same screen), a "View file" that has never worked, dates that render a day early for every US customer, billing states that lie under load, and a vendor reminder email that tells vendors to "log in" to an account they don't have. Pat can now complete the core job — but the product can quietly stop being true afterwards, which for a compliance tool is the difference between a 6 and an 8.

## Per-page scores (2026-06-03 rubric)

| Page | Baseline | Today | ≥ 8/10? | What gates it |
|---|---|---|---|---|
| Landing / marketing | — | 7 | **NO** | Header overflows ≤390 px phones (FP-012); CTA contrast (FP-005); promise drift (FP-011, FP-020) |
| Register | — | 7 | **NO** | `?plan=pro` takes money-intent and delivers Free silently (FP-030); toast-only errors (FP-033) |
| Login | — | 7 | **NO** | Lockout/rate-limit guidance lives in 4-second toasts (FP-033) |
| Verify email | — | 7 | **NO** | "token" jargon, logged-out dead-ends (FP-037) |
| Forgot / reset password | — | 5 | **NO** | False "Check your email" on failure (FP-031); expired-link dead-end (FP-032) |
| Dashboard (first-run) | — | 7 | **NO** | Hard-zeros flash before checklist swap (FP-046) |
| Dashboard (with data) | — | 4 | **NO** | Stale Compliant vs live Expired contradiction (#257); API error renders as empty account (FP-040); duplicated activity feed (FP-043) |
| Onboarding flow | — | **8** | **YES** | Modal → checklist → deep links all work; only minor dismissal traps (FP-046) |
| App shell / navigation | — | 6 | **NO** | Silent session eviction (FP-045); no help/support affordance (FP-048) |
| Documents list | — | 7 | **NO** | Day-early dates (#263); perpetual "Awaiting review" (FP-063) |
| Upload flow | — | 5 | **NO** | Silent rejection of oversize/HEIC/wrong-type (#265); no progress (FP-055) |
| Document detail | — | 5 | **NO** | "View file" dead (#254); "Read again" destroys edits unconfirmed (FP-062); vendor invisible (FP-065) |
| Vendors list | — | 6 | **NO** | Can't answer "who's not OK?" (FP-074); no delete (FP-073) |
| Vendor detail | — | 4 | **NO** | Duplicate checklist names ×2 in prod data (FP-070/#260); assignment never re-grades (#257); error hangs as "Loading…" (FP-072) |
| Requirements / checklists | — | 5 | **NO** | Summary teaches a false model (FP-083); DeleteRule 500s (#269); outage renders as "None yet" (FP-082) |
| Reminders | — | 5 | **NO** | History can't name the document (FP-090); silent no-send paths (FP-091); toggle race (#264) |
| Export | — | 6 | **NO** | "To" day silently excluded (#262); CSV literacy (FP-102) |
| Settings | — | 6 | **NO** | Raw IANA timezone wheel (FP-112); password rules post-hoc (FP-113) |
| Billing / upgrade | — | 4 | **NO** | Delete keeps charging (#255); loading/error masquerade as Free + live Upgrade tiles (FP-111); post-checkout race (FP-114) |
| Vendor portal (happy path) | — | 7 | **NO** | "Processing…" never resolves (FP-121); fake-personalized instructions card (FP-122) |
| Vendor portal (edge states) | — | 4 | **NO** | Transient load failure misdirects to "link is dead, ask Pat" (FP-120) |

**Pages clearing the ≥8/10 bar: 1 of 21 (Onboarding flow).** Overall 6/10. The bar is reachable: the P0/P1 list below is dominated by a small number of mechanical clusters (verdict staleness, file access, date rendering, billing states, error-as-empty) rather than rewrites.

## P0 catalog (ownership at a glance)

| # | P0 | Owner |
|---|---|---|
| 1 | Compliance verdicts are write-once: no expiry sweep, no re-eval on rule/assignment change, vacuous "Compliant" on zero applicable rules (LLM overwrites user-declared type), dashboard contradicts itself, stale status exported to auditors | **#257** (amended) |
| 2 | "View file" has never worked — raw private-blob URL, live-confirmed HTTP 409 | **#254** (live-confirmed) |
| 3 | Deleting a paid account never cancels the Stripe subscription | **#255** (re-confirmed) |
| 4 | Stripe webhook marks event processed BEFORE handling (`BillingEndpoints.cs:148-156` dedupe row saved, `:158` handler runs after) — a transient handler failure permanently eats a paid checkout | **#268** |
| 5 | Every date renders one day early in US timezones (live-demonstrated: stored `2026-11-01T00:00:00Z` → "10/31/2026" in America/Chicago) | **#263** (live-confirmed) |
| 6 | Dashboard dropzone silently swallows rejected files | **#265** (re-confirmed) |
| 7 | Free-plan fences unenforced (portal ignores `HasVendorPortal` + `DocumentLimit`); settings tile says "Vendor portal: Off" while it works | **#261** (re-confirmed) |
| 8 | "Monthly" extraction budget never resets — lifetime ceiling permanently kills extraction; UI promises "resumes next cycle" | **#256** (re-confirmed) |
| 9 | Extraction robustness: 2000-token default truncates real COIs, deploy-burned retries, no per-attempt timeout, no backoff, 20k-char OCR silently truncated, >15-page PDFs always fail (Document AI sync-API page limit; no page-count guard or batch fallback in `DocumentAiOcrService.cs`) | **#259** (amended; zombie-claim half RETRACTED as env artifact) |
| 10 | Vendor reminder email contains no upload link and tells vendors to "Log in" — vendors have no accounts; landing sells the opposite | **#241** FP-092 |
| 11 | "Read again" silently destroys manual field corrections (hover-only tooltip is the sole warning) | **#241** FP-062 |
| 12 | Portal load failure: transient network/5xx renders "This link is no longer available — ask your customer for a fresh link" with no retry | **#241** FP-120 |
| 13 | Dashboard API error renders a paying customer's account as brand-new empty (no `isError` anywhere on the page) | **#241** FP-040 |
| 14 | Dev environment IS production: shared Neon DB + Azure storage; local boots can auto-migrate prod; Railway worker races local queue consumers | **#271** (ops) |
| 15 | Deleting a requirement 500s forever once any document was checked against it (`Restrict` FK + hard delete) — Pat cannot loosen her own checklist | **#269** |
| 16 | Reminder sends that fail are recorded then permanently deduped — a 30-second Resend outage at 08:00 silently drops that day's warnings forever | **#270** (BOTH halves are send-semantics changes gated on a short ADR in the 0002/0007/0015 family — ADR 0002/0015's Neutral clauses explicitly defer failed-send retry to a future ADR) |

## Findings by surface

Severity: P0 blocks the job / locks out / data loss / trust destroyer · P1 major friction or broken promise · P2 polish. Each carries evidence and a fix sketch; FP-ids are referenced by the #241 punch list. Items owned by a standalone bug ticket cite it instead of an FP id.

### Regression tests the fixes must pin (per ticket)

- **#268**: extend `StripeWebhookTests.cs` — handler throws ⇒ event NOT recorded as processed, Stripe replay applies the subscription; the existing duplicate-id dedupe test stays green after the reorder.
- **#269**: integration — create rule → evaluate a document against it → DELETE the rule ⇒ 2xx + coherent check rows (today: FK-restrict 500).
- **#257/FP-083**: unit — template whose rules all target type Y + document of type X ⇒ NOT Compliant; sibling test that the worker no longer clobbers a user-declared `DocumentType`.
- **#270**: seed a `ReminderLog` row with `Status='failed'`, advance the injected `TimeProvider` ⇒ resend occurs (post-ADR).
- **#263**: render test under `TZ=America/Chicago` — stored `2026-11-01T00:00:00Z` displays 11/1/2026 (both list and detail render paths).
- **FP-040**: MSW 500 on `/api/dashboard/stats` ⇒ error card + retry; asserts NEITHER the zeroed grid NOR the Get-started checklist renders.
- **FP-120**: portal info network failure ⇒ retry affordance; dead-link copy reserved for 404/410.
- **FP-031**: forgot-password mutation rejection ⇒ no "Check your email" card; a 200 still shows the neutral confirmation (pins the anti-enumeration property).

### Cross-cutting visual & input (FP-001…FP-006, FP-010)

- **FP-005 [P1] Every primary CTA fails WCAG AA contrast.** `--primary: #0EA5E9` + white = 2.77:1; `--accent: #F97316` + white = 2.80:1 (`frontend/src/app/globals.css:58-65`) feed the default Button (`components/ui/button.tsx:16`), marketing CTAs (`app/page.tsx:37-44,410,428`, `site-header.tsx:57-60`), and the remediation CTA (`documents/[id]/page.tsx:179`). `text-primary` as body link/eyebrow fails the same way (`page.tsx:41-53,399,456`, `contact/page.tsx:65-71`). Fix: darken tokens once — `--primary` → sky-700 `#0369A1` (5.9:1) for text/fills with white fg, `--accent` → orange-600/700; body-link usages → existing `text-sky-700`.
- **FP-010 [P1→P2 sweep] Meaningful `text-slate-400` at 2.57:1.** Requirement group headers (`rules/page.tsx:601`), disabled-Add reason (`:716`), "(optional)" (`register-form.tsx:240,246`), and the icon-only trash/pencil/copy/revoke affordances (`documents/page.tsx:602`, `rules/page.tsx:523,532`, `vendors/[id]/page.tsx:238`) — the destructive controls are the faintest things on screen. (Countdowns/timestamps already sit at slate-500+, which passes AA — not part of this sweep.) Fix: slate-500+ for text, slate-500/600 for action icons; reserve slate-400 for decoration.
- **FP-001 [P1] Form-field boundaries are 1.2–1.4:1 — inputs effectively invisible.** `--input: #BAE6FD` = 1.33:1 (`globals.css:68`); filter selects use `border-slate-200` ≈ 1.17:1 (`documents/page.tsx:61-62`, `DocumentTypeSelect.tsx:47`). WCAG 1.4.11 wants ≥3:1 and the border is the only cue on white cards. Fix: darken `--input` to ~3:1 or add `bg-slate-50` fill.
- **FP-002 [P2] Focus indicators blend (≈1.67:1 ring).** `focus-visible:ring-3 ring-ring/50` (`button.tsx:12`, `input.tsx:12`); both dropzones define no focus style at all (`documents/page.tsx:232-238`, `portal/[token]/page.tsx:358-367`). Fix: full-opacity `ring-sky-600`; explicit dropzone `focus-visible:`.
- **FP-003 [P2] Wrong mobile keyboards / missing autofill.** Add-vendor email is `type="text"` (`vendors/page.tsx:84` — live-verified); vendor-detail email/phone untyped (`vendors/[id]/page.tsx:124-125,251-262`); doc-detail extracted fields ignore `fieldType` (`documents/[id]/page.tsx:545-550`); register lacks `autoComplete="name"/"organization"` (`register-form.tsx:190-207`). Fix: thread `type`/`inputMode`/`autoComplete` (the rules money form at `rules/page.tsx:684` already does it right).
- **FP-004 [P2] No "taking longer than usual" state during extraction.** A crashed claim can sit in the 5-minute zombie window with the UI saying only "Reading…"; the sole escape is knowing to click "Read again". Fix: after N minutes in Reading, add "taking longer than usual — we're retrying" (UI half pairs with #259's backend timeout).
- **FP-006 [P2] Sub-44px touch targets outside Button/Input primitives.** PasswordInput eye 32×32 (`PasswordInput.tsx:34`), portal "Retry upload" ~30px (`portal/[token]/page.tsx:444-450`), inline assign-Cancel (`documents/page.tsx:544-550`), "Forgot your password?" 12px (`login/page.tsx:79`), native selects h-9 (`documents/page.tsx:61`, `settings/page.tsx:318`). Fix: apply the existing coarse-pointer min-height utilities.

### Landing & marketing (FP-011…FP-014)

- **FP-012 [P1, NEW, live-found] The marketing header overflows every ≤390 px phone — the whole landing page pans horizontally.** The logo link renders 211 px wide and doesn't shrink; with the CTA (156 px authed "Go to dashboard" / ~120 px "Get started") + 44 px burger + padding the row needs ~415–447 px (`site-header.tsx:71-76`, `Logo` at `height={36}`). Live-measured at 375 px: `scrollWidth` 431. The responsive classes are correct — the content is simply too wide. Fix: smaller logo below `sm` (height ~28) and/or shorten the authed CTA to "Dashboard" below `md`.
- **FP-011 [P2] Landing copy vs. reality drift.** (a) "Reminders go out at 60, 30, 14, and 7 days… to you and straight to your vendor" (`page.tsx:201`) — the seed only sets `NotifyVendor` for ≤30-day rungs (`AuthEndpoints.cs:143-153`); (b) the Pro card + FAQ attribute the vendor link and audit export to Pro while nothing gates them server-side (see #261); (c) venue page promises reminders "as the event approaches" (`coi-tracking-for-event-venues/page.tsx:156-158`) — there is no event-date concept. Fix: align copy or seeds/gates (#261 decides the gate).
- **FP-013 [P2] Auth pages have no titles and are indexable.** No `metadata` export under `(auth)`; `robots.ts:8-18` doesn't exclude them. Fix: per-page titles + `robots: { index: false }`.
- **FP-014 [P2] Footnote contrast on the dark pre-footer.** "No credit card. Cancel anytime." `text-sky-300/60` ≈ 3.99:1 (`page.tsx:538`). Fix: raise opacity / sky-200.

### FAQ & content truth (FP-020)

- **FP-020 [P1] FAQ promises "You always review and confirm what CompliDrop extracted before it counts" (`faq/page.tsx:48`) — false.** Live walk: high-confidence extraction went straight to a green "Compliant" with no review step. Fix: truthful copy ("we flag anything uncertain for your review") — or build a confirm gate (out of scope).

### Auth flows (FP-030…FP-037)

- **FP-030 [P1] `?plan=pro|annual` registration silently creates a Free account.** Banner sells "Start your Pro account" (`register-form.tsx:59-65`, `plans.ts:153`); submit pushes `/dashboard` (`register-form.tsx:151`); backend always seeds `Plan="free"`, 5-doc cap (`AuthEndpoints.cs:130-141`). Pat believes she subscribed; weeks later hits the cap. Fix: hand off to Stripe checkout after signup (or land on Settings billing with the tile pre-selected) + commit-point copy "you'll set up payment next."
- **FP-031 [P1] Forgot-password shows "Check your email" even when the send failed.** `catch { } finally { setSent(true) }` (`forgot-password/page.tsx:28-36`); the backend already 200s unknown emails, so a throw is a real failure (network, 429). Fix: flip the card only on success; error → "We couldn't send the email — try again in a minute."
- **FP-032 [P1] Expired reset link (45-min TTL) dead-ends in a vanishing toast over a live form.** `reset-password-client.tsx:53-55` toasts the server message; no "Request a new link" path (the missing-token branch at `:58-72` has one). Fix: on `auth.reset_invalid`, swap to the error card with a "Request a new link" button.
- **FP-033 [P1] Login/register server outcomes are toast-only.** Wrong password, the lockout message (the only copy telling Pat how to get back in — `AuthEndpoints.cs:926-931`), 429s, and duplicate-email 409 all evaporate in ~4 s (`login/page.tsx:46-49`, `register-form.tsx:152-155`). Fix: persistent inline `role="alert"` above the submit; duplicate-email gets "Sign in instead" + "Reset your password" links.
- **FP-034 [P2] Forgot/reset/settings forms drop the error-association pattern.** Bare error `<p>`s, no `aria-invalid`/`aria-describedby` (`forgot-password/page.tsx:67-68`, `reset-password-client.tsx:96-102`, `account-management.tsx:93-94,152-153,274-275`) — login/register wire both. Fix: copy the login pattern.
- **FP-035 [P2] Reset-password lacks the live password checklist** (`reset-password-client.tsx:41-45` vs `register-form.tsx:71-102,140`). Fix: reuse `PasswordChecklist` + `mode:"onTouched"`.
- **FP-036 [P2] Success states swap in without focus or announcement** (`forgot-password/page.tsx:38-52`, `reset-password-client.tsx:74-83`; verify-email does it right with `role="status"`). Fix: `role="status"` on the card or focus the heading.
- **FP-037 [P2] Verify-email jargon + logged-out dead-ends.** "This link is missing its token" (`verify-email-client.tsx:38`); "Continue to dashboard" bounces a logged-out phone user to /login unexplained (`:39,:52,:101-109`). Fix: human copy + "Sign in to continue."

### Dashboard, onboarding & shell (FP-040…FP-049)

- **FP-040 [P0] An API hiccup renders Pat's account as brand-new and empty.** `hasData` defaults true on error (`dashboard/page.tsx:47`), every stat falls back `?? 0` (`:80-115`), activity error shows the empty state (`:163-164`), and the Get-started checklist re-appears unchecked (`GetStartedChecklist.tsx:85`). No `isError` is read anywhere; `StaleDataBanner` exists but is unused here. Fix: gate grid/checklist on `stats.isSuccess`; render the error card + Retry on failure.
- **(#257) [P0] "Compliant" count and rate contradict the "Expired" bar on the same screen.** Stats count stored `ComplianceStatus` (`DashboardEndpoints.cs:26-27`) while expired counts live dates (`:32,:75-76`); nothing ever re-evaluates (see the engine cluster under Requirements). Owned by #257.
- **FP-043 [P1] Recent activity is duplicated machine noise that omits the hero moment.** Live-confirmed: "Vendor added" ×2, "Vendor updated" ×2, "Document uploaded"+"Document added" in the same second (interceptor row + explicit `IAuditLogger` row: `VendorEndpoints.cs:90-92`, `DocumentEndpoints.cs:364-365,382`, `ComplianceEndpoints.cs:79`). Unmapped keys print API-ese: portal upload → "Vendor Portal Link · Upload Processed" (`VendorPortalEndpoints.cs:164-169` vs `display-labels.ts:108-110`), `vendorportallink.updated`, `compliancecheck.created` ×N per evaluation. Extraction completion never appears (worker has no user — `AuditSaveChangesInterceptor.cs:72,104`). Fix: whitelist curated actions in the endpoint; drop the redundant explicit calls for entity mutations (root cause per the CLAUDE.md audit rule); add labels "Vendor sent a document" / "Upload link emailed"; add a system "document processed" event.
- **FP-045 [P1] Session expiry is a silent eviction that loses Pat's place.** Expired session nulls the cache (`query-client.ts:71-78`), layout redirects with no message (`layout.tsx:142-148`), login never explains and always lands `/dashboard` (`login/page.tsx:45`). Fix: `/login?expired=1` + "You were signed out to keep your account safe" + `returnTo` — `returnTo` must be validated as a same-origin relative path (starts with `/`, not `//`; never an absolute URL) or it becomes an open-redirect phishing vector.
- **FP-046 [P1] Cold loads flash hard zeros; first-run swaps grid→checklist; one stray click ends the tour forever.** Zeros while loading (`dashboard/page.tsx:47,:80-115` — only activity got skeletons); backdrop/Escape persist `HasCompletedOnboarding` (`WelcomeModal.tsx:55-64` → `dashboard/page.tsx:33-39`) with recovery buried in Settings and no `onError` on the completion mutation (`useAuth.ts:196-202`). Fix: skeletons gated on `isLoading`; branch grid-vs-checklist only on `isSuccess`; treat backdrop-dismiss as minimize; handle mutation errors.
- **FP-042 [P1] The numbers don't reconcile for Pat.** "Expired" counts superseded documents forever (no archive/supersede notion — `DashboardEndpoints.cs:32,:75-76`); compliance rate counts not-yet-evaluated docs in the denominator (`:54` — live: "0%" right after first upload reads as an indictment); "Expiring ≤ 30d" (math notation, `dashboard/page.tsx:82`) and "Next 30 days" (`:128`) are the same fact labeled twice. Fix: latest-doc-per-(vendor,type) bucketing, exclude unevaluated from the rate, one label.
- **FP-041 [P1] Filters aren't URL-addressable and stat cards/buckets don't click through.** Verified: `documents/page.tsx` has no `searchParams` — the status/type/expiry filters are local state, so "3 non-compliant" on the dashboard can't deep-link to those 3. Fix: `?status=&expiresWithin=` params + clickable stat cards/pipeline buckets.
- **FP-044 [P2] "COI" is unexpanded at first in-app use** (`WelcomeModal.tsx:23`, `GetStartedChecklist.tsx:55`). A direct-link signup never saw the landing's expansion. Fix: "insurance certificates (COIs)" once per surface.
- **FP-047 [P2] Toasts render top-right on phones, covering the top bar** (`providers.tsx:28` — `position="top-right"` unconditional). Attempt 1 observed live interception; position re-verified in source. Fix: bottom-center on coarse pointers.
- **FP-048 [P2] Error copy says "contact support"; the shell offers no way to do it** (nav has seven entries, no Help — `layout.tsx:16-24`; `display-labels.ts:273-291` instructs contacting support). Fix: mailto Help in the sidebar footer.
- **FP-049 [P2] Shell odds and ends.** Desktop sidebar isn't sticky (`layout.tsx:182`); dashboard never refreshes itself (no `refetchInterval` in `useDashboard.ts:37-59`, `refetchOnWindowFocus:false` global) so "Still being read: 1" freezes — gate the new interval on `pendingExtraction > 0` (the conditional-polling pattern in `useDocuments.ts:85-97`) so idle dashboards stay quiet, since one dashboard tick is ~14 count queries; verify-email banner has no "wrong address?" escape (`EmailVerificationBanner.tsx:41-54`); zero-count pipeline buckets draw a 6%-tall colored bar (`dashboard/page.tsx:225`); activity rows have no entity names and raw `toLocaleString()` timestamps (`DashboardEndpoints.cs:90-97`, `dashboard/page.tsx:169-170`); login noise crowds the 6-slot feed (`AuthEndpoints.cs:210-215`). Fixes per item.

### Documents — list & upload (FP-051, FP-053…FP-056; #263, #265)

- **(#265) [P0, re-confirmed] Rejected files vanish in total silence** — `onDrop(accepted)` only, no `fileRejections`/`onDropRejected` (`documents/page.tsx:132-147`); the portal solved exactly this (`portal/[token]/page.tsx:62-84`). HEIC parity rides along: dashboard accept-list omits HEIC the backend supports (`:141-146` vs `FileValidationService.cs:49-58`) — draft FP-051 folds into #265's fix.
- **(#263) [P0, live-confirmed] Dates render one day early for US users.** Stored `2026-11-01T00:00:00Z` (this walk's real extraction) renders **10/31/2026** in America/Chicago/New_York/Los_Angeles via `toLocaleDateString()` (`documents/page.tsx:570`, `[id]/page.tsx:500`); the SQL day-diff is UTC-correct, so Texas would see "10/31/2026 · in 144d" — date and countdown visibly inconsistent, and the reminder email/CSV show the correct face date. Fix: render the ISO date portion TZ-agnostically (`timeZone:"UTC"` or string slice).
- **FP-053 [P2] "Extraction" column header is the jargon the badges avoid** (`documents/page.tsx:429`, mobile `data-label` `:563` — detail page already says "Reading"); expired countdown reads "5d ago" (`:573`). Fix: header "Reading"; "expired 5 days ago".
- **FP-054 [P2] An active status filter hides the doc you just uploaded** (filter state vs refetch — `documents/page.tsx:83-90`): toast says uploaded, list shows nothing. Fix: clear filters or banner "1 new document hidden by filters."
- **FP-055 [P2] No per-file progress; one vendor silently applies to a whole staged batch** (`documents/page.tsx:314-317,161-164`; singular copy `:257` with multi-file staging `:132-137`). Fix: per-file spinner/check; pluralize copy "All {n} files will be assigned to this vendor."
- **FP-056 [P2] Cost-ceiling failure copy promises auto-recovery that never happens** ("It resumes next cycle" — `display-labels.ts:274-275`; nothing re-queues, and per #256 the budget never resets anyway). Fix: truthful copy + pairs with #256.

### Document detail (FP-060…FP-067; #254)

- **(#254) [P0, live-confirmed] "View file" is dead for every document.** The link is the raw private blob URL (`[id]/page.tsx:461-470`; container `PublicAccessType.None`, `BlobStorageService.cs:27,39`; no SAS anywhere, no proxy route) — live click → **HTTP 409** Azure XML. The "Double-check this" hints ask Pat to verify against a file she can never open. Danger note: the tempting ops "fix" (flip container public) would expose every customer COI. Fix per #254: authenticated streaming proxy.
- **FP-062 [P0] "Read again" silently destroys manual corrections.** Sole warning is a hover `title` (`[id]/page.tsx:455`) — invisible on touch; `PersistSuccess` deletes and recreates all fields with `IsManuallyEdited=false` (`ExtractionWorker.cs:278-292`) and re-runs compliance off the raw extraction. Fix: `ConfirmDialog` (exists for delete) at least when edited fields exist: "This replaces the N values you corrected."
- **FP-063 [P1] "Awaiting review" can be forever and nobody says so.** No requirement set (or eval threw) parks the doc Pending (`ComplianceCheckService.cs:58-66`) → badge "Awaiting review" (`display-labels.ts:15-21`) — no reviewer exists. Fix: when `complianceChecks.length === 0 && vendorId != null`, render "No requirements set for {vendor} yet — add some so we can check this document" + link; consider a distinct "Not checked yet" label.
- **FP-064 [P1] "Needs your review" with zero fields is a dead end.** Zero extracted fields → `ManualRequired` (`ExtractionWorker.cs:294-300`); amber card commands checking fields that don't exist ("No details read yet." — `[id]/page.tsx:221-225,532-537`); no add-field UI though `UpdateFields` can create server-side (`DocumentEndpoints.cs:426-436`). Fix: swap copy + minimal manual-entry form (expiration date, GL limit).
- **FP-065 [P1] The vendor is invisible on the page that is the document's home.** `vendorName/vendorId` fetched (`[id]/page.tsx:56-58`) but surfaced only inside the non-compliance explainer (`:181-199`); no link, no assign affordance for orphans (list-only — `documents/page.tsx:553-560`). Fix: Vendor header line + link; inline `VendorPicker` when null.
- **FP-067 [P1] Between retries Pat sees "We couldn't read this document — contact support" for a file still in flight.** Transient failure sets `ProcessingError` while status returns to Pending (`ExtractionWorker.cs:214-221`); the card renders whenever `processingError` is set (`[id]/page.tsx:508`). Fix: gate on `extractionStatus === "Failed"`.
- **FP-061 [P2] Stale "Why isn't this compliant?" card survives a requirement-set removal** (no-template branch never clears old `ComplianceCheck` rows — `ComplianceCheckService.cs:58-66` vs `:68-69`; explainer renders on any failed check `[id]/page.tsx:153-155`). Fix: clear checks in the no-template branch.
- **FP-066 — DROPPED.** The draft's "duplicate field labels on multi-policy COIs" did not re-derive: the pipeline writes one row per canonical field name, so duplicate labels shouldn't occur. Attempt-1's evidence no longer exists; re-file only with a concrete repro.
- **FP-060 [P2] Detail page has no delete** (list rows only — `documents/page.tsx:578-605`). Fix: same `ConfirmDialog`-guarded remove in the header.

### Vendors (FP-070…FP-076)

- **FP-070 [P1, live-confirmed in prod data] The checklist dropdown shows every system template twice with identical names.** Live: 11 options, "Caterer" ×2 etc. — the prod DB still contains both the 2026-04-16 and 2026-06-04 seed sets (data cleanup is #251); the UI compounds it: flat `{t.name}` options (`vendors/[id]/page.tsx:136-138`), clones keep the same name (`rules/page.tsx:95-131`), no ownership grouping. Fix here: `<optgroup>` "Your checklists"/"Suggested" + clone name suffix. The missing **server-side template-ownership validation** (`VendorEndpoints.cs:109` binds any FK, and the `SystemDbContext` evaluation path would grade against a cross-org template) is a tenant-isolation gap split out to **#273** — not dropdown polish.
- **(#257-adjacent) [P0] Assigning or changing a checklist never re-grades existing documents** (`VendorEndpoints.cs:96-114` saves and stops; the amber warning at `vendors/[id]/page.tsx:143-154` promises the opposite; `POST /api/compliance/check/{id}` exists but no frontend caller). Pat's natural portal-first walk leaves docs "Awaiting review" forever. Owned by #257 (amended). **Implementation constraint for #257's fan-out:** template-wide re-evaluation must be set-based (one query + `ExecuteDeleteAsync` + bulk insert), audit-suppressed (one summary row, not `compliancecheck.created` ×N — today every check row also writes an AuditLog row with Before/After JSON), and enqueued/debounced rather than inline in the rule-mutation request — the per-document `EvaluateAsync` loop would mean thousands of round trips (with a sync `RemoveRange` enumeration) inside one HTTP call at 1000-doc scale. Per-vendor re-assignment (tens of docs) can stay inline.
- **FP-072 [P1] Any vendor-detail load error hangs as "Loading vendor…" forever** (`vendors/[id]/page.tsx:29-31`; `isError` unhandled). Fix: error branch mirroring the vendors-list card (`vendors/page.tsx:150-191`).
- **FP-071 [P1] No view of the vendor's documents anywhere on the vendor page;** list "Docs 3" is dead text (`vendors/page.tsx:209`); documents page has no vendor filter param (`documents/page.tsx:514-516`). Fix: "Documents from {vendor}" card + make the count link `/documents?vendor={id}` (pairs with FP-041).
- **FP-073 [P1] A vendor can never be removed, and duplicates are easy to create.** `useDeleteVendor` exists unused (`useVendors.ts:88-98`); no duplicate-name hint (`VendorEndpoints.cs:68-94`). Fix: Remove-vendor behind `ConfirmDialog` + "You already have a vendor named X" hint. (Backend NRE on delete → #269.)
- **FP-074 [P1] The vendors list can't answer "which vendors are not OK?"** — no status/coverage column (`vendors/page.tsx:126-130`); with per-document verdicts only, a vendor missing a required document type is flagged nowhere. Fix: per-vendor rollup ("Covered / Action needed / Missing: license") on list + detail (pairs with FP-083). Compute it server-side inside the existing single `ListVendors` projection (`VendorEndpoints.cs:28-42`) — never per-vendor round trips — and land it AFTER #257, since a rollup over write-once stale verdicts would institutionalize the staleness.
- **FP-075 [P2] Link-row affordances mislead.** Copy renders for inactive links with a success toast (live code `vendors/[id]/page.tsx:220-231`); the bare `await navigator.clipboard.writeText` has no failure handling (live-hit this walk: clipboard denial → generic "Something went wrong. Try again." AFTER a successful generate — and "try again" would mint a second link); revoke is a one-tap unconfirmed ✕ (`:236-240`) with no success toast (`useVendors.ts:128-142`). Fix: hide/mute Copy on inactive rows + say why inactive; try/catch with the existing fallback pattern (`:90-98`); `ConfirmDialog` + toast on revoke.
- **FP-076 [P2] Add-vendor form: Enter doesn't submit, typo'd emails accepted silently** (inputs not in a `<form>`, no email format check — `vendors/page.tsx:76-103`, `VendorEndpoints.cs:83`; failure surfaces later as a generic send error). Fix: `<form onSubmit>` + `type="email"` + light validation; send-failure copy suggests checking the address. Mobile: portal-link row is a five-item non-wrapping flex (`vendors/[id]/page.tsx:219-241`) — `flex-wrap`, URL on its own line.

### Requirements / checklists (FP-080…FP-086)

- **FP-083 [P0] The green summary teaches a false model — and no screen can say whether a vendor is covered.** Summary reads "A Transportation/Shuttle is compliant when every document proves: … auto liability …; Holds a CDL license; …" (`rules/page.tsx:466-477`) but the engine scopes rules per document type (`ComplianceCheckService.cs:71-73`): a COI is never asked about the license, and a never-uploaded license is invisible everywhere. Worse, a document matching zero rules of its type is stamped **Compliant** (`:75,92-96`) — and extraction overwrites the user-declared type with the LLM's classification (`ExtractionWorker.cs:247`), so a caterer's permit gets a green badge against zero checks. Fix: zero-applicable-rules → Pending/"No requirements apply to this type" (#257 owns the engine half); reword the summary per-type ("Each certificate of insurance must prove… / Each license must prove…"); per-vendor rollup (FP-074).
- **FP-080 [P1 on phones, live-confirmed] Tapping a checklist appears to do nothing on mobile.** Live: "Use this" cloned the checklist but the editor rendered at viewport-top 1973 px on an 812 px screen — 2.4 screens below the fold, no scroll/focus/toast (`rules/page.tsx:179` grid stacking). Fix: `scrollIntoView` (or collapse the rail) on selection below `md`.
- **FP-082 [P1] A backend failure renders as "None yet — create one above."** `templates.isError/isLoading` never branched (`rules/page.tsx:160-222`): outage = empty rails, zero suggested checklists, no retry. Fix: rail skeletons + error card + Retry (the vendors list pattern).
- **FP-081 [P1] Nothing prevents adding the same requirement type twice.** Verified: the Add-a-requirement menu renders `REQUIREMENT_TYPES` with no already-added filter (`rules/page.tsx:594-603`); duplicates produce confusing double sentences (and double failures). Fix: gray out with "Already added — edit it instead" + backend dedupe on `(templateId, documentType, fieldName, operator)`.
- **FP-084 [P2] Money field silently turns "2M" into $2 and "1.5" into $1** (`requirements.ts:65-70` strips non-digits; `rules/page.tsx:682-689`). A $2 GL minimum passes every COI on earth — an always-green requirement. Fix: k/m suffix support + "Did you mean $2,000,000?" warning under ~$10k.
- **FP-085 [P2] The seeded Security Service certification rule is orphaned in the sentence catalog** — no match for `certification` type (`ComplianceTemplateSeed.cs:120` vs `requirements.ts:218-229`), renders as a context-free fallback that reads like a duplicate, has no Edit affordance, and can't be re-added once deleted. Fix: add a Certifications group (or fold the doc type into the fallback sentence).
- **FP-086 [P2] Editor dead-ends after authoring; delete-confirm hides the blast radius.** No assign affordance (tip promises it — `rules/page.tsx:174-177`); "used by N vendors" isn't clickable (`:297-300`); the delete dialog doesn't say how many vendors lose their checklist though `vendorCount` is loaded (`:421-432` vs `:26-33`). Fix: "Assign to vendors…" link; interpolate the count.
- **(#269) [P0] Deleting a requirement 500s once any document has been checked against it.** `DeleteRule` hard-deletes (`ComplianceEndpoints.cs:164-171`; no `DeletedAt` on `ComplianceRule`, so the interceptor's soft-delete translation doesn't apply) against `ComplianceCheck → ComplianceRule` `ReferentialAction.Restrict` (`Migrations/20260416213651_InitialSchema.cs:374-378`). The trash button returns "An unexpected error occurred" every time after first evaluation — Pat cannot loosen her own checklist.
- **(#272) [P1] The "Professional liability (E&O)" requirement can essentially never pass** — the builder offers `professional_liability_limit` (`requirements.ts:113-123`) but the extraction prompt's COI field list omits it (`ExtractionPrompts.cs:25-27`) → `min_value` fails "Unable to parse numeric comparison" (`ComplianceCheckService.cs:124-127`) even when E&O is printed on the certificate. Same family: "Names you as additional insured" does `contains` against a field the prompt never specifies a format for (`requirements.ts:148-160`) — an ACORD checkbox read ("Y") flags honest certificates. Filed as #272.

### Reminders (FP-090…FP-095; #264)

- **FP-092 [P0] The vendor reminder email contains no upload link and tells the vendor to "Log in to {org} on CompliDrop"** — vendors have no accounts (`ReminderBackgroundService.cs:430-438` one shared body; `:205-206` adds the vendor recipient). The landing sells the exact opposite (`page.tsx:201`). The footer also claims cadence is adjustable in "Settings → Reminders" — cadence isn't editable anywhere and Reminders is top-level nav. Fix: recipient-aware bodies; vendor copy embeds the active portal link (mint if none); fix footer. Highest-leverage email change in the product.
- **FP-090 [P1] Send history can't answer "which document was that about?"** — only When/Recipient/Status rendered (`reminders/page.tsx:123-142`); `documentId/reminderId` fetched and dropped; backend never joins names (`ReminderEndpoints.cs:63-73`). Fix: document + vendor + rung columns, linked.
- **FP-091 [P1] Two silent no-send paths, never disclosed.** Vendor without contact email is skipped without trace (`ReminderBackgroundService.cs:205`; `ContactEmail` nullable); documents without an expiration date never match the window (`:188-196`). No "skipped" row is written. Fix: disclosure on the page ("3 vendors have no email — they won't get reminders" / "2 documents have no expiration date") or skipped history rows. **Disclosure half only** — catch-up/retry semantics are #270.
- **(#264) [P1, re-confirmed] Toggle race:** PUT body merges the patch over possibly-stale cache (`reminders/page.tsx:42-53`); no optimistic update (`:84-103`) so taps visibly lag, inviting the double-taps that trigger the race. Owned by #264 (with the blank-vendor-name half, not re-derived but unchallenged).
- **FP-094 [P1] History/ladder error states lie.** Failed or pending history fetch renders "No reminders sent yet." (`reminders/page.tsx:120-121`); the ladder renders a bare header table while loading/on error (`:80`). False "nothing has ever been sent" in the trust-critical direction. Fix: `isError` branches + retry + skeleton.
- **FP-093 [P2] "Lead time" header is logistics jargon; "30 days before" never says before what** (`reminders/page.tsx:73,82`). Fix: "When" + "30 days before a document expires" + an intro sentence naming who gets emailed.
- **FP-095 [P2] Mobile: the deliveries table lacks `overflow-x-auto`** (`reminders/page.tsx:111-145` inside `overflow-hidden` Card) so the Status column (the bounced one) clips once rows exist — code-verified; live page was empty-state. The 4-column switch table fits at 375 (live-measured 279 px) — attempt-style "Active off-screen" claims don't reproduce. Fix: `overflow-x-auto` or `.stacked-table`.
- *(Backend — #270)* failed sends permanently deduped (`alreadySent` selects on existence not status — `ReminderBackgroundService.cs:244-273`) + missed 08:00 tick never caught up (`ReminderBackgroundService.cs:397-403`, exact-day targeting `:171`) + `EmailBodyTemplate` stored but ignored (`ReminderEndpoints.cs:53` vs `ReminderBackgroundService.cs:258-260`) + UTC-midnight expiry makes "14 days" emails fire 15 days out in US zones (`ReminderBackgroundService.cs:177-193,434`). Both send-semantics halves (failed-send retry AND catch-up) are gated on a short ADR in the 0002/0007/0015 family — ADR 0002/0015's Neutral clauses explicitly defer failed-send retry to a future ADR.

### Export (FP-101…FP-102; #262)

- **(#262) [P1, re-confirmed] The audit window silently excludes the entire "To" day** (`ExportEndpoints.cs:25-26` midnight parse; `ExportService.cs:36` `<= to`) while the PDF prints "events from X to Y" (`:102-104`) and the page defaults To=today (`export/page.tsx:15,136-140`). Owned by #262.
- **FP-101 [P1] Export downloads need the shared 401-refresh-retry helper** (bare-fetch blob paths; the pattern exists in `ExportDataButton`). Fix: extract + reuse.
- **FP-102 [P2] Export literacy cluster.** Default date range computed in UTC shows tomorrow in "To" for evening US users (`export/page.tsx:136-140`); CSV header `Status` holds extraction state next to `Compliance` (`ExportService.cs:168-184`); leading raw GUID column; `"u"`-format timestamps Excel can't parse; filename embeds the To-date the CSV ignores; no client validation for empty/inverted ranges (`export/page.tsx:38-40`). Fixes per item.

### Settings & billing (FP-111…FP-115; #255, #256)

- **(#255) [P0, re-confirmed] Account deletion never cancels Stripe** (`AuthEndpoints.cs:774-817` — `IStripeService` not even injected; no cancel call exists anywhere; UI copy never mentions billing — `account-management.tsx:263-265`). Pat can't log back in to fix it. Owned by #255 (+ checkout guard below).
- **FP-111 [P1] Billing card: loading, error, and 404 all render "You're on the free plan" + live Upgrade tiles** (`settings/page.tsx:104,126-129,177-206`; no `subscription.isError` branch) — to a paying customer mid-outage, with tiles that can start a second checkout. Fix: skeleton + error card; tiles only when the subscription loaded and is genuinely free.
- **FP-114 [P1] Post-checkout race + no already-subscribed guard.** Success toast fires off the URL param alone (`settings/page.tsx:99-102`) while the DB flips only on webhook (`StripeService.cs:125-138`); checkout never refuses an active paid sub (`BillingEndpoints.cs:24-81`) and a second completes by overwriting `StripeSubscriptionId` (`StripeService.cs:131`), orphaning the first. Fix: poll ~30 s "Activating your plan…" + hide tiles; server-side reject when active (the guard half belongs to #255).
- **FP-115 [P1] No renewal date, no pending-cancellation state.** `currentPeriodEnd` fetched, never rendered (`settings/page.tsx:23-31`); `cancel_at_period_end` not stored (`StripeService.cs:148-157`) — after cancelling, Settings shows zero acknowledgment. Fix: "Renews/Ends on {date}"; store + surface the flag.
- **FP-112 [P1] Timezone picker is ~400 raw IANA ids starting at Africa/Abidjan** (`settings/page.tsx:314-325`, `lib/timezones.ts:24-36`). The live next-send preview below it is excellent — finding the zone is the problem. Fix: curated US-first labels ("Central Time — America/Chicago") + "More…".
- **FP-113 [P2] Settings/billing polish cluster.** "AI reading cost" dollar figure with the reassurance in 10px type (`settings/page.tsx:167-173`); "Vendor portal: Off" tile with no explanation or path (`:161-166` — and it's untrue today, see #261); Founding "First 50 only." enforced nowhere (`BillingEndpoints.cs:44-50`) while Founding $39/mo strictly dominates the *featured* Annual; password rules appear only as post-submit errors (`account-management.tsx:23-27,96-107`); "Export your data" sits under the red Danger zone (`:170-184`); post-downgrade "12 / 5" documents tile with no explanation (`StripeService.cs:163-166`, `settings/page.tsx:156-159`). Fixes per item.

### Vendor portal (FP-120…FP-125)

- **FP-120 [P0] Any transient load failure tells Tony the link is permanently dead — and to go bother Pat.** `fetchInfo` catch leaves `info`+`error` null (`portal/[token]/page.tsx:149-155`); the only `!info` branch renders "This link is no longer available. Ask your customer for a fresh upload link." (`:309-323`); no retry; no timeout on the fetch (black-hole = skeleton forever). Fix: distinct network-error state + Try again; reserve dead-link copy for the 404/410 codes the backend already discriminates (`VendorPortalEndpoints.cs:33,35`).
- **FP-121 [P1, live-confirmed] "Processing…" never resolves; Tony never learns he's done.** Static label (`:476`); the status endpoint built for this (`VendorPortalEndpoints.cs:205-229`) has zero frontend callers; the server's success message is discarded (`:182-184`). Live: quota line also still said "0 / 20 uploads used" after a successful upload (initial-fetch only — `:396`, `atQuota` `:274`). Fix: poll status a few times → "Received — you're all set, you can close this page"; count `uploaded.length` into quota/atQuota.
- **FP-122 [P1, live-confirmed] "What {org} needs from you" is fake personalization.** The backend hardcodes one generic sentence (`VendorPortalEndpoints.cs:43`); `VendorPortalLink` has no instructions column; `GeneratePortalLink` accepts none — the card titles boilerplate as Pat's specific ask. Fix: retitle neutrally now ("What to upload"); real owner-instructions channel as the bigger half.
- **FP-124 [P1] Mid-upload network failure: the file-preserving Retry exists in state but renders only on the rate-limit branch** (`:191-195` vs `:428-456`); multi-file batches stop at the first failure without naming it, silently never attempting the rest (`:232-241`). Fix: render Retry whenever `retryFile` is set; per-file error naming; continue or report the batch remainder.
- **FP-123 [P2] Portal edge cluster.** Exhausted link presents as the generic dead-link (last upload flips `IsActive` — `VendorPortalEndpoints.cs:144` → PortalInfo 404; the "Upload limit reached" dropzone state `:381` is unreachable on fresh load); dropzone stays enabled mid-upload with no progress bar and the POST has no Idempotency-Key (double-tap = duplicate doc + burned permit — `:288`, `VendorPortalEndpoints.cs:52-198`); formats copy says "PDF, JPEG, or PNG" while HEIC is accepted (`:394` vs `:282-285`); client `maxSize` equals the Kestrel cap exactly so a 9.99 MB file dies generically (`:287` vs `Program.cs:282`); 0-byte files reach the server's wrong message (no `minSize` — `:276-289`); rate-limit copy invites a futile immediate retry (`:441-451`); revoked-link screen stacks two near-identical sentences (`:313-319`); tab title is the marketing default (no metadata export). Fixes per item.
- **FP-125 [P2] WebP/GIF rejected with copy claiming he didn't send "a photo"** (`:67-72`, accept list `:278-286`). Fix: transcode server-side (ImageTranscoder exists) or reword.

### Portal & forms accessibility (FP-130…FP-131)

- **FP-130 [P1] Portal upload success/progress never announced to assistive tech** — errors get `role="alert"` (`portal/[token]/page.tsx:401-407`) but "Uploading…" and the Received card are visual-only (`:398,467-481`); a blind vendor can't tell whether to retry (duplicate uploads burn quota). Plus: dropzones lack any focus style; VendorPicker filters silently with a permanently-true bare `aria-expanded` (`VendorPicker.tsx:117-121`); sub-44px portal Retry. Fix: `aria-live="polite"` region (the documents page has the pattern — `documents/page.tsx:191-216`); focus styles; combobox semantics + result-count live region.
- **FP-131 [P2] A11y stragglers.** Error association missing on forgot/reset/settings forms (see FP-034); instructions scroll-box not keyboard-reachable (`portal/[token]/page.tsx:352` — `max-h-48 overflow-y-auto` without `tabIndex`); native selects h-9 on touch; success-card focus management (FP-036).

## Attempt-1 claim reconciliation (the distrust list)

| Claim | Verdict |
|---|---|
| "Stale Processing docs never reclaimed; claims wedge mid-attempt" (#259, #243 comment) | **Artifact, explained.** Dev shares the prod DB; Railway's worker is a second consumer that re-claims (resetting `ProcessingStartedAt`) and completes work the local observer never sees. Demonstrated live: a Pending doc was claimed 2.1 s after reset while the local API was dead. The code-level halves of #259 (attempt increment on interrupted claims, no per-attempt timeout) remain real. Clean single-process repro requires the #243 integration-test route. |
| "Dev Resend emails never deliver" | **Already corrected before this audit** — they deliver; the negative searches hit the Gmail-MCP wrong-account trap. Not re-litigated. |
| #260 root cause: rename-window vs concurrent double-seed | **Rename-window confirmed.** Duplicate `IsSystemTemplate` rows carry `CreatedAt` 2026-04-16 21:52 and 2026-06-04 16:19 — two seed events seven weeks apart, not a same-instant race. **The duplicates are still live in prod today** (this walk's dropdown showed every checklist twice) — #260's closure missed the data cleanup. |
| Register pre-hydration password-in-URL | **Dev-mode artifact, dropped.** `/register` is statically prerendered; production ships a form-less skeleton until hydration. |
| Documents list "stuck on Waiting to read" | **Automation artifact.** Hidden-tab pause of `refetchInterval`; foreground tabs poll at 5 s. |

## What's genuinely good (calibration — don't break these)

The onboarding modal + state-aware checklist teach the loop and tick from real data; status badges pair icon + text + AA hues; `prefers-reduced-motion` is globally neutralized; stacked-card tables on documents/vendors are a strong phone pattern; the sentence-catalog requirement builder (money presets, numeric keyboard, honest "not expired" helper) keeps Pat away from operators entirely; "Email link to {vendor}" with the saved-email gate is exactly right; portal camera-first copy + HEIC + branded skeleton; the timezone next-send preview; dialogs/sheets/switches ride Base UI with correct focus behavior; skip link + `aria-current` + extraction live regions; upload→verdict in ~9 s with plain-English field labels throughout.

## Re-score addenda (#241 batch split)

The P0/P1 fix work was split into batches A–G (#315–#321; see #241). Each batch
appends its outcome here; the full per-page re-score to the >=8 exit bar lands
with the final batch once every gate on a page is cleared.

### Batch A — contrast & visibility sweep (#315, merged 2026-06-22)

Resolved the cross-cutting visual findings: **FP-005** (every primary/accent CTA
+ `text-primary` link now clears AA — `--primary` sky-700 5.9:1, `--accent`
orange-700 5.2:1), **FP-001** (input/select boundaries `--input` slate-500 4.77:1,
was 1.33:1), **FP-002** (focus rings full-opacity `--ring` sky-600, explicit
dropzone rings, all routed through the token), **FP-010** (meaningful slate-400
text + action icons → slate-500), **FP-014** (dark pre-footer footnote → sky-200).
A self-bug found in review — the token darkening pushed the two dark-section
`SectionLabel` eyebrows to 2.34:1 — was fixed in the same PR (sky-300 on dark).

**Effect on the score table:** contrast is no longer a gating factor on any page;
`contrast-tokens.test.ts` pins the WCAG ratios so it can't silently regress. No
page is *re-scored to 8 yet* — the pages whose contrast was a named gate still
carry non-contrast gates from later batches (e.g. Landing still needs FP-012
header overflow + FP-011/020 promise drift, both Batch B). Per-page numbers move
when those land.

### Batch B — truth, money-path & marketing (#316, merged 2026-06-22)

Closed the trust/honesty cluster: **FP-031** (forgot-password no longer shows a
false "Check your email" on a failed send; anti-enumeration preserved),
**FP-032** (expired reset link → recovery card, not a vanishing toast),
**FP-033** (login/register server errors persist inline; email-taken offers
Sign-in + Reset exits), **FP-035** (shared live PasswordChecklist on reset),
**FP-037** (verify-email drops "token" jargon; logged-out → "Sign in to
continue"), **FP-013** (auth pages noindex + per-page titles), **FP-030**
(paid-plan signups land on Settings billing with honest copy; full Stripe
handoff stays #31), **FP-111** (billing card gates tiles on the loaded
subscription — no "free + Upgrade" during an outage), **FP-114** (post-checkout
polling instead of a URL-param toast), **FP-115** (Renews-on date; the
renew-vs-end distinction needs `cancel_at_period_end` → #323), **FP-012**
(marketing header fits <=390px), **FP-020** (truthful FAQ flagging copy),
**FP-011** (reminder/venue copy aligned to reality), **FP-063** (a "not checked
yet" doc explains the cause + fixes it inline — orphan vendor picker / set-up
link; also lands FP-065's orphan picker), **FP-064** (manual entry replaces the
Failed/zero-field dead end).

**Effect on the score table:** Forgot/reset password (was 5) and Billing/upgrade
(was 4) clear their named gates here — but Billing's remaining 8-bar also assumed
#255 (landed) so it should re-measure ~8; Auth (Register/Login/Verify, was 7)
clear their toast-only + jargon gates. Document detail (was 5) loses the
"awaiting-review dead end" gate (FP-063) but still needs FP-062 "Read again"
confirm + FP-065's vendor-name link (Batch C) to reach 8. **Deferred:** #323
(cancel_at_period_end), #324 (the "Awaiting review"→"Not checked yet" relabel —
the explainer makes the current label coherent meanwhile). Final per-page table
lands with the closing re-score once Batches C–G complete.

### Batch C — documents & detail (#317, merged 2026-06-22)

Closed the documents-list + document-detail cluster: **FP-062** (P0 — "Read
again" now confirms before discarding hand-corrected/unsaved fields, naming the
count), **FP-065** (the vendor name shows as a link to the vendor page; the
orphan-assign picker already shipped in #316), **FP-067** (the "we couldn't
read this — contact support" card shows only on terminal Failed, not between
automatic retries), **FP-060** (delete from the detail page, same ConfirmDialog
as the list), **FP-041** (P1 — the documents filters are URL-addressable:
`?status=`/`?type=`/`?expiresWithin=`/`?vendor=` seed the view and mirror back,
and the dashboard stat cards + the Expired/Next-30 pipeline buckets deep-link
into the filtered list — "1 non-compliant" is now one click away), **FP-053**
("Extraction" list header → "Reading"; "5d ago" → "expired 5 days ago"),
**FP-054** (a heads-up when an active filter may hide a just-uploaded doc),
**FP-055** (per-file upload spinner + count-aware staging copy). **Already
resolved by earlier merges:** FP-051 (#265 dropzone), FP-056 (#256 made the
cost-ceiling reset real — the copy already states the monthly reset), FP-061
(#269 already clears stale ComplianceCheck rows in the no-template branch).

**Effect on the score table:** Document detail (was 5) clears its named gates —
"View file" (#254), "Read again" destroying edits (FP-062), vendor invisible
(FP-065) — and should re-measure ~8. Documents list (was 7) clears the
day-early dates (#263) + the dead-end "Awaiting review" (FP-063/#316) and gains
deep-linkable filters. Upload (was 5) keeps #265's rejection feedback + now
per-file progress (FP-055). The closing per-page re-score table lands once
Batches D–G complete (dashboard/shell, vendors/requirements, reminders/export/
settings, portal/a11y).

### Batch D — dashboard & shell (#318, merged 2026-06-22)

Closed the dashboard-trust + app-shell cluster: **FP-040** (P0 — a stats outage
now renders an error card + Retry, gated on `stats.isSuccess`; NEITHER a zeroed
grid NOR the first-run checklist can render on failure — the "paying account
looks brand-new-empty" lie is gone; the activity feed gets its own error
branch), **FP-042** (compliance rate excludes not-yet-graded docs from the
denominator, so a fresh upload no longer flashes "0%"; the "Expiring ≤ 30d" math
notation → "Expiring within 30 days"), **FP-043** (the activity feed is now a
fail-closed *whitelist* — `compliancecheck.created` ×N, `documentfield.*`,
`vendorportallink.updated`, the internal `user.updated` flip, and login noise no
longer leak as entity-speak; the portal upload reads "Vendor sent a document",
link emails "Upload link emailed"; a new system `document.processed` event puts
extraction completion in the feed; and the redundant explicit `IAuditLogger`
calls that duplicated the interceptor's entity-mutation rows
(`vendor.created/updated/deleted`, `complianceTemplate.*`,
`vendorPortalLink.created`, `document.uploaded`) were dropped, de-polluting the
audit export per the CLAUDE.md rule), **FP-044** ("insurance certificates
(COIs)" spelled out at first in-app use — modal slide 1 + the checklist hint),
**FP-045** (P1 — session expiry now lands on `/login?expired=1` with a "you were
signed out to keep your account safe" notice and a `returnTo`; the `returnTo` is
validated as a same-origin RELATIVE path — `safeReturnTo` rejects `//host`,
`/\host`, absolute URLs, and control chars — closing the open-redirect vector; a
deliberate log-out still lands on a plain `/login`), **FP-046** (skeletons
instead of hard zeros while stats load; grid-vs-checklist branch only on success;
the welcome modal treats backdrop/Escape as *minimize* — a stray click no longer
ends the tour forever — while the X / Skip / final CTA complete it; `onError` on
the completion mutation), **FP-047** (toasts move to bottom-center on coarse
pointers so they stop covering the mobile top bar), **FP-048** (a "Help &
support" mailto in the sidebar footer — the shell had no path for the "contact
support" error copy), **FP-049** (sticky desktop sidebar; dashboard self-refresh
gated on `pendingExtraction > 0` so "Still being read: N" unfreezes without
polling idle dashboards; a "Not your email?" escape on the verify banner;
zero-count pipeline buckets draw no bar; relative time in the feed; login noise
filtered via the FP-043 whitelist), and **FP-004 (UI half)** (a document stuck in
"Reading…" past ~2 minutes shows a "taking longer than usual — we'll keep
retrying automatically" reassurance, pairing with #259's backend timeout/reclaim).

**Effect on the score table:** Dashboard (first-run) (was 7) clears its
hard-zeros-flash gate (FP-046) and should re-measure ~8. Dashboard (with data)
(was 4) clears all three named gates — the stale Compliant-vs-Expired
contradiction (#257, landed), the API-error-as-empty-account (FP-040), and the
duplicated activity feed (FP-043) — and should re-measure ~8. App shell (was 6)
clears the silent-session-eviction (FP-045) and no-help-affordance (FP-048)
gates and should re-measure ~8. **Deferred** (real tickets under #150): the
FP-042 *supersession* half — bucketing Expired by latest-doc-per-(vendor,type) —
is a cross-surface data-semantics change that must stay consistent across the
dashboard, documents list, reminders, and export, so it warrants its own ADR
rather than a one-screen edit (**#327**, `bug`); and the FP-049
*entity-names-in-the-feed* half needs polymorphic name resolution
(Document/Vendor/Template → name) in the RecentActivity query (**#328**, `task`).
The closing per-page re-score table lands once Batches E–G complete.
