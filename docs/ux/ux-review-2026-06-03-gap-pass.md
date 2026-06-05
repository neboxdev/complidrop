# CompliDrop — UX review, gap pass (2026-06-03)

> **Why this pass exists.** The first fresh review ([`-multiagent.md`](ux-review-2026-06-03-multiagent.md)) optimized for copy/IA/flow and **under-covered mobile, accessibility, performance, forms behavior, and functional escape-hatches** (it found 0 mobile and only 3 a11y issues). This pass sent 8 agents back at exactly those dimensions, each told to ignore the ~20 already-catalogued pass-1 issues and surface only **net-new** problems, verified against the code (and the backend where relevant).
>
> **Result: 104 net-new findings (16 blockers, 45 major)**, plus independent confirmation of all 9 suspected prior-review gaps. Combined with pass 1, the two fresh passes have produced ~336 findings.

## Verdict

**The 3/10 stands — and this pass shows it's generous for Pat's real context.** Pass 1 catalogued copy/flow problems *assuming a working desktop surface*. This pass proves that assumption false on the device Pat and her vendors actually use: **the authenticated app is structurally unusable on a phone**, and one root cause (`(dashboard)/layout.tsx:42` hard-codes `style={{ gridTemplateColumns: "240px 1fr" }}` as an **inline** style with no media query, hamburger, or drawer) cascades into every dashboard page. You cannot fix "NonCompliant" copy on a screen Pat can't open.

## 8 net-new themes

1. **The authenticated app is desktop-only.** The inline 240px sidebar grid eats ~62% of a 390px phone, with no hamburger/drawer — and because it's an *inline style* it beats any Tailwind `md:` fix by specificity (the fix is itself a trap). Gates the value of every pass-1 copy fix.
2. **Core tables are clipped, not scrollable; dense forms never stack.** `documents` (7 cols) and `vendors` (5 cols) sit in `overflow-hidden` (other tables use `overflow-x-auto`, proving it's an oversight) → compliance status, Expires, and the "Manage" link are cut off on a phone. Every `grid grid-cols-2`/`grid-cols-5` form grid lacks a responsive prefix, squeezing inputs to 70–180px.
3. **Touch targets are systematically <44px — and the destructive ones are smallest.** Button `h-8`/`h-7`, Input `h-8`, reminder toggle 20×36px with a 12px thumb, the document-delete trash at 28px fronting a native `confirm()`. A fat-finger deletes a document or silently flips a compliance-email toggle.
4. **Status is conveyed by hue alone, below-AA contrast, and never announced.** Compliant vs Non-compliant are identical pale pills; `text-slate-400` (~2.63:1, fails AA) carries the days-until-expiry countdown, the OCR field-diff, and all "Loading…" text — Pat's stated aging-eyes barrier. Polling silently swaps Processing→Compliant with no `aria-live`, so a blind user never learns extraction finished.
5. **The app shell ships zero a11y scaffolding and no reduced-motion handling.** No skip-to-content link, unlabeled `<nav>`, no `aria-current`, no route-change announcement; `animate-pulse`/`animate-spin` run unbounded with no `prefers-reduced-motion`.
6. **Form validation is invisible to assistive tech.** Error `<p>`s have no `id`/`aria-describedby`; validation is on-submit-only (all errors dumped at once after the keyboard dismisses); no `setFocus` to first error; no password show/hide; missing `autoComplete` blocks phone autofill; optional fields (Industry/Size) look required.
7. **True one-way doors & data-loss traps (verified against the backend).** **No email verification** (register validates `Contains('@')` then issues cookies → a typo kills every reminder forever); **timezone is write-once** from a browser guess with no update endpoint (silently controls the 08:00 send hour); **documents list sends no pagination params** so 26+ docs are unreachable while the header says "N total"; a **transient `/me` 500 evicts a valid session**; **no change-password/change-email/delete-account/data-export** (lockout + GDPR/CCPA exposure).
8. **Public-surface trust & mobile gaps.** Marketing footer has **no Privacy/Terms/Contact** — a trust gap *and* a hard Stripe Checkout prerequisite before charging cards; marketing nav is `hidden md:flex` with no hamburger; the **vendor portal** shows desktop "Drag…click" copy with no camera-capture affordance (`accept="image/*"`) for a vendor photographing a COI, and a brand-less "Loading…".

## Top 22 net-new findings

| # | Sev | Surface | Issue | Fix (short) | Eff |
|---|----|---------|-------|-------------|-----|
| 1 | 🔴 | `layout.tsx:42` | Fixed 240px **inline-style** sidebar, no drawer → eats 62% of a phone | Drop inline style; `grid-cols-1 md:[240px_1fr]`, `hidden md:flex` aside, mobile top-bar + hamburger → Sheet drawer | L |
| 2 | 🔴 | documents:120, vendors:62 | Tables in `overflow-hidden` clip status/Expires/Manage | `overflow-x-auto`; better: stacked cards below `md` | M |
| 3 | 🔴 | `AuthEndpoints.cs` | Signup email never verified → typo kills all reminders | Tokenized verify email (Resend wired) + `EmailVerified`; persistent "confirm your email" banner until verified | L |
| 4 | 🔴 | reminders:151 | Reminder toggle fails a11y + touch + low-vision at once | Real Base UI `switch` (role/aria-checked/aria-label), focus ring, non-color cue, 44px hit area, more spacing | M |
| 5 | 🔴 | settings:76 | Timezone write-once, no update endpoint → wrong 08:00 hour forever | `PUT /api/auth/organization` (timezone + org name) + labeled IANA `<select>`; show next send time | M |
| 6 | 🔴 | useDocuments:44 | List renders only page 1, no pager → 26+ docs unreachable | Thread page/pageSize + pager; surface the status/type/vendor/expiry filters the backend already implements | M |
| 7 | 🟠 | vendor edit, register, settings, export, dashboard | `grid-cols-2/5` never stack → 70–180px fields | Add `grid-cols-1 sm:grid-cols-2` / `grid-cols-2 sm:…md:grid-cols-5` (one-token edits) | S |
| 8 | 🟠 | register-form, login | Validation errors invisible to SR; on-submit-only | `aria-describedby`+`id` per field; `mode:'onTouched'`; `setFocus(firstError)` | M |
| 9 | 🟠 | layout:30, useAuth | Transient `/me` 500 evicts a valid session to /login | Redirect only when `me.data===null` (not `!me.data`); show retry-in-shell on error | S |
| 10 | 🟠 | site-footer:13 | No Privacy/Terms/Contact — **Stripe Checkout prerequisite** | Add legal/contact row: Privacy, Terms, Support; gates billing go-live | S |
| 11 | 🟠 | button/input.tsx | Touch targets <44px; destructive icons smallest | `@media (pointer:coarse)` min-height 2.75rem, or `size-11` on mobile for delete/revoke/copy/retry | M |
| 12 | 🟠 | documents:215, dashboard, detail | Status by hue only; detail Expired/ExpiringSoon collapse to slate | Leading status icon (survives grayscale); fix detail badge colors; pair label text with pass-1 enum work | M |
| 13 | 🟠 | documents:223, detail:316, dashboard | `text-slate-400` (2.63:1) on real data (expiry countdown, OCR diff) | Bump meaningful uses → `slate-500` (4.78:1); reserve slate-400 for decoration | S |
| 14 | 🟠 | layout:41 | No skip link, unlabeled `<nav>`, no `<main>` id | `sr-only focus:not-sr-only` skip link → `<main id>`; `aria-label`; `aria-current` | S |
| 15 | 🟠 | documents:229, vendor detail | Icon-only delete/revoke/copy have no accessible name | `aria-label` (the rules page already does this — apply the pattern) | S |
| 16 | 🟠 | portal:319 | Desktop "Drag…click" copy, no camera capture, bare loading | "Tap to choose a file or take a photo"; `accept:'image/*,application/pdf'`; branded skeleton | M |
| 17 | 🟠 | documents polling, detail | Doc completion never announced to AT | Off-screen `role="status" aria-live="polite"` on the terminal status transition | M |
| 18 | 🟠 | MapAuthEndpoints, settings | No change-password/email/delete-account/data-export | Settings "Security" + danger-zone backed by real endpoints (pairs with forgot-password) | L |
| 19 | 🟠 | globals.css, documents:19 | No `prefers-reduced-motion`; pulse/spin run unbounded | `motion-safe:` gate or a global reduced-motion safety net | S |
| 20 | 🟠 | register-form:150, login:65 | No password show/hide (blind-typing 12-char on a phone) | Reusable `PasswordInput` with Eye/EyeOff `aria-pressed` toggle | S |
| 21 | 🟠 | rules NewRuleRow | Unlabeled selects, no numeric keyboard, disabled Add gives no reason | Fold into the rules rebuild: stacked mobile form, labeled controls, `inputMode="numeric"`, reason on disabled Add | M |
| 22 | 🟡 | export:93, confirm() sites | Inverted date range submits; native `confirm()` not keyboard-safe | Disable Download when `from>to`; replace `confirm()` with accessible AlertDialog (also closes the pass-1 confirm copy item) | S |

> Full per-dimension detail (104 findings) is in `.claude/ux-gap-surfaces.json`; structured synthesis in `.claude/ux-gap-synthesis.json`.

## Prior-review gaps — all 9 CONFIRMED in code

The prior review's findings the fresh pass-1 missed are now verified, with evidence:

1. **Mobile P0 — CONFIRMED & worse than "unpolished."** `layout.tsx:42` inline `gridTemplateColumns: "240px 1fr"`, no media query/hamburger/drawer; inline style overrides `md:` by specificity. + `overflow-hidden` tables + unconditional `grid-cols-2/5`.
2. **Reminder-toggle a11y — CONFIRMED.** Bare `<button>`, hue-only state; grep for `role="switch"`/`aria-checked` across the frontend = **zero hits**.
3. **Email verification — CONFIRMED absent.** `Register()` validates only `IsValidEmail` then logs in; no verify route, no `EmailVerified` field.
4. **Editable timezone — CONFIRMED absent.** Set only at register from the browser guess; no org PUT/PATCH; `settings:76` is read-only text.
5. **Session-expiry/transient-error eviction — CONFIRMED.** `!me.data` redirect fires on any non-401 error (`useMe` maps only 401→null).
6. **List search/filter + pagination — CONFIRMED absent on the frontend** (response type carries `total/page/pageSize`; the call sends none).
7. **Legal footer — CONFIRMED absent** (3 columns, none legal; bottom bar is tagline + copyright).
8. **Portal `instructions` never rendered — CONFIRMED** (typed in `PortalInfo`, absent from the JSX).
9. **Native `confirm()` for deletes — CONFIRMED** (+ the net-new a11y angle: no focus return, unstyleable, thread-blocking).

See [`-comparison.md`](ux-review-2026-06-03-comparison.md) for the final merged roadmap that absorbs all of this.
