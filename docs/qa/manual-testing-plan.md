# CompliDrop — Manual QA Testing Plan

> **Scope:** every user-facing workflow before launch.
> **Audience:** the tester (you) driving a real browser against a real running environment.
> **Companion:** [`README.md`](README.md) for how to use, [`test-fixtures.md`](test-fixtures.md) for what files to prepare, [`bug-report-template.md`](bug-report-template.md) when something breaks.

This is not a code test. Unit + integration tests (`api/CompliDrop.Api.Tests`, `frontend/src/**/*.test.tsx`) cover correctness at the boundary level. Your job is the experience: what does a real human see, feel, click, type, get confused by?

Every step uses three lines:

> **Do** — the concrete action
> **Expect** — what the user should see, with exact copy in quotes where verifiable
> **Don't expect** — common failure modes you'd otherwise miss

If a step fails, log a bug ([`bug-report-template.md`](bug-report-template.md)), change the checkbox to `[!]`, drop the issue link beside it, **don't fix it inline**, keep moving.

> **This plan was last reconciled against the app on 2026-06-05**, after the large UX overhaul (PRs #181–#197, #216, #220). Every "Expect" below was ground-truthed against the current components. Status labels, button text, and flows changed substantially from the pre-overhaul plan — if reality and this plan disagree, the **app is the source of truth**: file a doc-fix, not necessarily a product bug.

## Table of contents

- [§0 — Setup & ground rules](#0--setup--ground-rules)
- [§1 — Smoke check (5 minutes)](#1--smoke-check-5-minutes)
- [§2 — First-time user journey (happy path, ~25 min)](#2--first-time-user-journey-happy-path-25-min)
- [§3 — Authentication & account flows](#3--authentication--account-flows)
- [§4 — Document management](#4--document-management)
- [§5 — Vendors](#5--vendors)
- [§6 — Vendor portal (external persona)](#6--vendor-portal-external-persona)
- [§7 — Vendor requirements (checklists)](#7--vendor-requirements-checklists)
- [§8 — Reminders](#8--reminders)
- [§9 — Export](#9--export)
- [§10 — Billing](#10--billing)
- [§11 — Settings](#11--settings)
- [§12 — Dashboard](#12--dashboard)
- [§13 — Multi-tenancy isolation](#13--multi-tenancy-isolation)
- [§14 — Edge cases & error states](#14--edge-cases--error-states)
- [§15 — Accessibility & polish](#15--accessibility--polish)
- [§16 — Known limitations (NOT bugs)](#16--known-limitations-not-bugs)
- [§17 — Performance & feel](#17--performance--feel)
- [§18 — Sign-off checklist](#18--sign-off-checklist)

---

## §0 — Setup & ground rules

Spend 30 minutes here. Skipping it wastes hours later.

### 0.1 Environment

- [ ] **Local dev** — both `dotnet watch run` (API on `:5292`) and `npm run dev` (frontend on `:3000`) are running and reachable. **OR** **staging deploy** — Railway API + Vercel frontend on staging URLs.
- [ ] **Database** — migrations apply **automatically on API startup** now (`Database:AutoMigrate`, default on; boot-time drift guard — see [ADR 0016](../adr/0016-apply-ef-migrations-on-startup.md)). So just booting the API against a fresh/behind DB brings the schema current. You can still run `dotnet ef database update --context AppDbContext` manually on your dev DB.
- [ ] **All secrets configured** — list per [CLAUDE.md](../../CLAUDE.md):
  - [ ] `Jwt:Secret` set
  - [ ] `AzureStorage:ConnectionString` set
  - [ ] `DocumentAi:*` configured (or `DocumentAi:Enabled=false` if testing without OCR)
  - [ ] `Extraction:Provider` set (`gemini` default)
  - [ ] `Gemini:ApiKey` (if `Endpoint=aistudio`) OR Vertex AI credentials
  - [ ] `Stripe:*` set to **test-mode** keys
  - [ ] `Resend:ApiKey` + `Resend:FromEmail` + `Resend:WebhookSecret` set (the last one gates the inbound delivery-status webhook)
- [ ] **Stripe** — confirm test-mode in the Stripe dashboard ("View test data" toggle ON). If live-mode is on by accident, stop and switch.
- [ ] **Frontend `.env.local`** — `NEXT_PUBLIC_API_URL` points at the right backend.

### 0.2 Test data

- [ ] Fixture folder created per [`test-fixtures.md`](test-fixtures.md) — including the new `.heic` photo for the iPhone-upload test (§6).
- [ ] Stripe test cards memorized: `4242…` (success), `4000 0000 0000 9995` (decline).
- [ ] Email aliases ready (`ruben+qaA@`, `ruben+qaB@`, `ruben+vendor@`). **You will now receive verification, password-reset, and reminder emails** — keep the inbox open.
- [ ] Resend dashboard tab open so you can verify outbound emails.

### 0.3 Browser setup

- [ ] Two browser profiles ready:
  - **Browser A** = "QA Admin A" (Chrome)
  - **Browser B** = "QA Vendor / Admin B" (Firefox or Edge — must differ from A so cookies don't bleed)
- [ ] DevTools open in both. **Network + Console panels visible.**
- [ ] Mobile device handy with the staging URL (or `ngrok` of localhost if on local dev). The app now has a real mobile shell (#181) and the portal supports camera capture (#196) — mobile is a first-class surface, not an afterthought.

### 0.4 Test discipline (read every time)

1. **One bug at a time.** If a test step fails, log it and *keep going*. Cascading failures often share a root cause and fixing one fixes many.
2. **Quote exact strings.** "Toast says 'Bad Gateway'" is actionable. "Got an error" is not.
3. **Capture correlation IDs.** Every response carries an `X-Trace-Id` response header (DevTools → Network → failing request → Headers); error envelopes also carry the same id as `error.correlationId` in the Response body. Paste either into the bug report.
4. **Watch the Console.** A React warning or an uncaught promise that doesn't reach the UI is still a bug — it'll surface in Sentry on launch.
5. **Don't trust visual memory.** If you think the button changed color between two clicks, screenshot both. Memory lies.
6. **Each section assumes the previous account state.** §2 creates "Admin A". §3 reuses A. §13 creates "Admin B" in Browser B. Don't blow these away mid-plan.

### 0.5 Anti-expectations for the entire app (verify continuously)

These are project rules — if any appear, file a bug immediately, marked **launch-blocker**:

- [ ] **No raw HTTP jargon** in any toast or error card: "Bad Gateway", "Internal Server Error", "Failed to fetch", "TypeError", `(502)`, `(500)`, `NetworkError`. The generic fallback is **"Something went wrong. Try again."** — that's it (it's the `GENERIC_FALLBACK_MESSAGE` in [`api.ts`](../../frontend/src/lib/api.ts)).
- [ ] **No raw enum / field codes** rendered to the user: "NonCompliant", "ManualRequired", "general_liability_limit", "compliancetemplate.created". Everything is humanized (#188) — a leak is a bug.
- [ ] **No console errors** during normal navigation (warnings on hot-reload are fine, errors are not).
- [ ] **No exposed stack traces** in API responses (you'd see them in DevTools → Network → Response).
- [ ] **No flicker of an error before the data loads** — loading states must precede data, not the other way around.
- [ ] **No localhost or staging URLs** visible in production marketing copy (search the rendered HTML).

---

## §1 — Smoke check (5 minutes)

Before diving in, prove the app is alive.

- [ ] **1.1 Landing page loads.** Browser A → `http://localhost:3000/` (or staging URL). **Expect:** the marketing landing with headline **"Stop chasing certificates of insurance."** / **"Start dropping docs."** renders within 3 seconds. **Don't expect:** a 404, a blank white page, the old "Stop Chasing Paper." headline, or a Tailwind purge regression (unstyled HTML).
- [ ] **1.2 Header nav (logged out).** Top right shows **"Log in"** + orange **"Get started"**. On desktop (≥ md) a secondary nav row also shows **"Pricing" / "Event venues" / "FAQ" / "Glossary" / "Support"**. **Don't expect:** "Go to dashboard" — that's the logged-in variant.
- [ ] **1.3 Backend healthcheck.** In a new tab: `http://localhost:5292/health/live`. **Expect:** `200 OK`, JSON body with `status: "Healthy"`. **Don't expect:** a 500 or a database-disconnected response.
- [ ] **1.4 API CORS.** From the DevTools console on the landing page: `await fetch('http://localhost:5292/api/auth/me', {credentials:'include'})`. **Expect:** a 401 response (not a CORS block). **Don't expect:** a red CORS error in console — that means the API isn't allowing `localhost:3000`.
- [ ] **1.5 Register and login URLs exist.** Click **"Log in"** → `/login` loads. Back. Click **"Get started"** → `/register` loads.
- [ ] **1.6 How-it-works anchor.** On the landing, click **"See how it works ↓"** — page should scroll to the `#how-it-works` section smoothly (no jump-cut). Then scroll down manually to the pricing section (`#pricing`).

If any 1.* fails, stop and fix the environment. Don't proceed.

---

## §2 — First-time user journey (happy path, ~25 min)

The single most important test. This is what a real first customer experiences. Do it slowly, observing the *feel*, not just the function.

**Browser A. Fresh incognito or fresh profile.**

### 2.1 Landing → register

- [ ] **2.1.1** Land on `/`. **Expect:** hero with **"Stop chasing certificates of insurance." / "Start dropping docs."**, an eyebrow **"COI, license & permit tracking"**, two CTAs (orange **"Get started free"** with arrow + outlined **"See how it works ↓"**), and a sticky nav. Below the hero text: a coded **product-preview** mock (a fake `Acme-Catering-COI.pdf` window showing a green **"Compliant"** badge and an **"Expires in 23 days"** chip).
- [ ] **2.1.2** Hover the orange **"Get started free"** button. **Expect:** it darkens and lifts slightly with a shadow. **Don't expect:** a flicker, a layout shift, or a missing cursor change.
- [ ] **2.1.3** Scroll through the page. **Expect:** sections in order — **Problem** ("Your spreadsheet is a ticking time bomb."), **How it works** ("Three steps. Thirty seconds. Done."), a **social-proof "note from the team"** block (no fabricated testimonials/logos), **Pricing** ("Enterprise accuracy. Small-business pricing."), **Who it's for**, then the dark final CTA ("Drop your first document in under a minute."), then the footer. **Don't expect:** any image with broken `alt`, any "lorem ipsum" placeholder, any unstyled list.
- [ ] **2.1.4** Pricing section shows three tiles: **Free $0**, **Pro $49** (with orange **"Most popular"** badge), **Annual $39/mo** (with **"Billed $468/year — save $120"** subline).
- [ ] **2.1.5** Click the **Pro** tile's **"Get started →"** button. **Expect:** URL becomes `/register?plan=pro`; the form heading switches to **"Start your Pro account"** with a sky banner **"You selected the Pro plan — $49/month. Cancel anytime."** plus a **"Change"** link back to `/#pricing`.
- [ ] **2.1.6** Go back. Click the **Annual** tile's **"Get started →"**. **Expect:** `?plan=annual` and banner **"You selected the Annual plan — $39/month, billed $468/year. Save $120."**
- [ ] **2.1.7** Go back. Click the **Free** tile's **"Start free →"**. **Expect:** `?plan=free`, heading **"Start dropping docs"**, and NO banner (Free has no banner copy).

### 2.2 Registration form validation

Still on `/register?plan=free`:

- [ ] **2.2.1** Submit empty form. **Expect:** four inline red errors:
  - "Your full name is required"
  - "Company name is required"
  - "Enter a valid email"
  - "Password must be at least 12 characters"
  - **Don't expect:** a toast, a redirect, or only one error showing at a time.
- [ ] **2.2.2** Type `J` in Full name, `A` in Company. **Expect:** the name/company errors still show (min-2 chars).
- [ ] **2.2.3** Type a clearly bad email (`notanemail`). Submit. **Expect:** "Enter a valid email".
- [ ] **2.2.4** Fix the email to `ruben+qaA@gmail.com`. Type password `short`. Submit. **Expect:** "Password must be at least 12 characters".
- [ ] **2.2.5** Type `abcdefghijklm` (13 chars, all letters). Submit. **Expect:** "Password must include a digit".
- [ ] **2.2.6** Type `1234567890123` (13 chars, all digits). Submit. **Expect:** "Password must include a letter".
- [ ] **2.2.7** Watch the **live password checklist** under the field as you type a good password (`qa-launch-2026A`). **Expect:** three items that turn green as satisfied — **"At least 12 characters"**, **"A letter"**, **"A number"**. **Don't expect:** the old single help line "Min 12 chars, with a letter and a digit." (that was replaced by the checklist).
- [ ] **2.2.8** Note the assent line below the submit button: **"By creating an account, you agree to our Terms of Service and Privacy Policy."** with both as links (→ `/terms`, `/privacy`).

### 2.3 Successful registration

- [ ] **2.3.1** Fill the form:
  - Full name: `Ruben Garcia QA`
  - Company: `QA Admin A`
  - Email: `ruben+qaA@gmail.com` (or whatever email you control)
  - Password: `qa-launch-2026A`
  - Industry: (leave blank — optional)
  - Size: (leave blank — optional)
- [ ] **2.3.2** Click **"Create my account"**. **Expect:** button switches to **"Creating account…"** and disables.
- [ ] **2.3.3** Within ~1 second: redirect to `/dashboard`. **Expect:** success toast **"Account created. Welcome!"** appears bottom-right and auto-dismisses.
- [ ] **2.3.4** **Expect:** a **verification email** is sent (subject **"Confirm your email for CompliDrop"**) — check the inbox / Resend dashboard. The link expires in 7 days.
- [ ] **2.3.5** **Don't expect:** a flash of `/login` before `/dashboard`. **Don't expect:** an upgrade banner (you're on Free and the upgrade prompt lives only on `/settings`).

### 2.4 First impression of the dashboard (onboarding)

A brand-new org (0 documents) gets the onboarding experience, **not** the full stat grid.

- [ ] **2.4.1 Welcome modal.** **Expect:** a 3-slide welcome modal opens automatically. Slide 1 **"Stay audit-ready without the chase"**; slide 2 **"Four steps to covered"** (Add a vendor → Set what they must prove → Collect the document → Read the result); slide 3 **"Start with your first vendor"** with a final button **"Add your first vendor"**. There's an X (**"Skip the tour"**), Escape and backdrop also close it. **Do:** close it (Skip or finish). **Don't expect:** it to reappear on the next visit (it's gated on `hasCompletedOnboarding`).
- [ ] **2.4.2 Email-verification banner.** **Expect:** an amber banner near the top reading **"Confirm your email {email} so your compliance reminders actually reach you."** with a **"Resend email"** button. It has no dismiss — it stays until you verify. **Don't expect:** it to block the app (soft gate).
- [ ] **2.4.3 Greeting.** **Expect:** **"Welcome, Ruben"** (first name only). Subtitle: **"Here's a snapshot of where your vendors stand."**
- [ ] **2.4.4 Get-started checklist.** **Expect:** a **"Get started"** card ("A few steps to your first audit-ready vendor.") with a **"0 of 4"** counter and four steps: **"Add your first vendor"**, **"Choose what they must prove"**, **"Collect a document"**, and **"Expiry reminders are on"** (this last one is pre-checked/struck-through — reminders are seeded for you).
- [ ] **2.4.5 No KPI grid yet.** **Don't expect:** the 4-card KPI strip, secondary stats, or the expiry pipeline. For a 0-document org the stat grid is hidden until your first document lands. (You'll see it appear after §2.5.)
- [ ] **2.4.6 Bottom cards.** **Expect:** a **"Drop a document"** card (link **"Go to Documents →"**) and a **"Recent activity"** card showing **"No recent activity yet."** (or a brief loading skeleton). **Don't expect:** a broken empty state ("undefined", "null", a JS error in console).
- [ ] **2.4.7 Sidebar.** **Expect:** navy sidebar, 7 nav items in order: **Dashboard / Documents / Vendors / Vendor requirements / Reminders / Export / Settings**. Dashboard is highlighted (`aria-current="page"`). **Don't expect:** a nav item literally labeled "Rules" — the `/rules` route is labeled **"Vendor requirements"**.
- [ ] **2.4.8 Sidebar footer.** **Expect:** org name **"QA Admin A"** in bold, email below in grey, a sky badge **"Free"**, and a **"Log out"** button.

### 2.5 First document upload (now a two-step "add details" flow — #186)

- [ ] **2.5.1** Click **Documents** in the sidebar. **Expect:** `/documents` loads. Heading "Documents". Subtitle "COIs, licenses, permits — dropped once, tracked forever." Right side small text **"0 total"**. A dismissible sky tip card titled **"This is where documents land"** appears above the table.
- [ ] **2.5.2** **Expect:** a large dashed dropzone with cloud-upload icon and copy **"Drag a file here or click to browse"** + small line **"PDF, JPEG, PNG · 10 MB max"**.
- [ ] **2.5.3** Empty table state: **"No documents yet — drop a COI, license, or permit above and we'll read it and track its expiry for you."**
- [ ] **2.5.4** Drag `happy-path/sample-coi.pdf` over the dropzone. **Expect:** the dashed border activates and copy switches to **"Drop to add…"**.
- [ ] **2.5.5** Drop. **Expect (the new staging step):** nothing uploads yet. A card appears titled **"Add details before uploading"** with sub-copy **"Pick the vendor this is for so we can check it against their requirements."**, the staged filename, a **Vendor** picker, a **Document type** select (defaulting to **"Certificate of Insurance"**), an **"Upload 1 file"** button (disabled), a **"Cancel"** button, and the hint **"Choose a vendor to continue."**
- [ ] **2.5.6** In the **Vendor** picker, type `Mike's Electrical` and click **"Add new vendor "Mike's Electrical""**. **Expect:** the vendor is created inline and selected (shows as a pill with a **"Change"** button). The **"Upload 1 file"** button enables.
- [ ] **2.5.7** Leave **Document type** at **"Certificate of Insurance"**. Click **"Upload 1 file"**. **Expect:** button → **"Uploading…"**, then a toast **"Uploaded sample-coi.pdf"**, the staging card clears, and a new table row appears with the filename as a link, Vendor **"Mike's Electrical"**, and a slate extraction badge **"Waiting to read"** (with an hourglass icon). The counter reads **"1 total"**.
- [ ] **2.5.8** **Don't expect:** an upload before a vendor is chosen (vendor is required now). **Don't expect:** a progress bar (there isn't one — §16). **Don't expect:** the row to be missing until you refresh — TanStack Query re-fetches automatically.
- [ ] **2.5.9** Return to `/dashboard`. **Expect:** now that you have a document, the **KPI strip appears** (Total documents 1, etc.) and the **"Add your first vendor"** + **"Collect a document"** checklist steps flip to done.

### 2.6 Watch the extraction lifecycle (humanized statuses — #188)

- [ ] **2.6.1** On `/documents`, within ~5 seconds the extraction badge flips from slate **"Waiting to read"** to a pulsing sky **"Reading…"** (refresh icon, `motion-safe` pulse). Do not refresh — polling does this automatically. **Don't expect:** the old "Pending"/"Processing" enum labels.
- [ ] **2.6.2** Click the filename to open the detail page (`/documents/{id}`). **Expect:** "Loading document…" briefly, then the detail page.
- [ ] **2.6.3** Detail header: filename as h1, an editable **Type** select beside it, a **"All documents"** back link, a **"Read again"** button, and a **"View file"** link. **Don't expect:** a "Re-extract" button — it's labeled **"Read again"** now.
- [ ] **2.6.4** Four summary cells, labeled **Reading / Compliance / Expires / Verified**. The **Reading** cell shows the humanized extraction badge; **Compliance** shows slate **"Awaiting review"**; **Expires** shows `—`; **Verified** shows `—`.
- [ ] **2.6.5** Extracted-fields card heading **"Extracted fields"**; while still reading it shows **"Reading the document…"**.
- [ ] **2.6.6** **Wait 30–90 seconds** for extraction to complete. The page polls every 3s; you do **not** need to refresh.
- [ ] **2.6.7** The Reading badge flips to emerald **"Read"** (check icon). **Don't expect:** any confidence percentage on the badge (e.g. "Completed · 91%") — that copy is gone everywhere.
- [ ] **2.6.8** Extracted-fields card populates: a 2-column grid of editable inputs, each with a small field label (e.g. **"General liability limit"**, **"Expiration date"**) and a value. Low-confidence fields get a colored hint below — amber **"Double-check this"** (0.7–0.9) or rose **"Please verify"** (< 0.7) — and an amber/rose input border. **Don't expect:** a numeric "% confident" pill — it was replaced by these tiered hints (#188).
- [ ] **2.6.9** **Don't expect:** the **"Save changes"** button to be active yet (it's disabled until you edit something — except when the doc is in "Needs your review", see §4). **Don't expect:** any field tagged **"✎ Manually edited"** yet.
- [ ] **2.6.10** The **Expires** cell now shows a localized date (or `—` if the doc had none).
- [ ] **2.6.11** Open DevTools → Network. **Expect:** polling stopped — no `/api/documents/{id}` request every 3s anymore. **Don't expect:** continuous background traffic after extraction is done.

### 2.7 Edit and save a field (saving re-checks compliance — #216)

- [ ] **2.7.1** Find a field with a hint (amber/rose). If none exist, pick any field.
- [ ] **2.7.2** Edit the value (type something new). **Expect:** the **"Save changes"** button at the top of the fields card becomes enabled.
- [ ] **2.7.3** Click **"Save changes"**. **Expect:** toast **"Fields updated"**. The edited field now shows a sky tag **"✎ Manually edited"** and a small grey line **"was: {original value}"**.
- [ ] **2.7.4** The **Verified** summary cell now shows emerald **"Yes"** with a shield icon. **Don't expect:** the literal "✓ Yes" — it's a shield icon plus the word "Yes".
- [ ] **2.7.5 Compliance re-evaluation (#216).** If you edited a field a requirement depends on (e.g. raised `general liability limit` above a vendor's required minimum, or corrected the expiration date), **expect** the **Compliance** badge to update in place after the save (it refetches). A corrected value can flip **"Action needed"** → **"Compliant"**. **Don't expect:** a special "compliance re-checked" toast — the only toast is "Fields updated"; the badge (and the "Why isn't this compliant?" card, §4.7) just changes after the refetch. *(This won't visibly change anything until the vendor has a requirements checklist — you'll exercise it fully in §7.)*
- [ ] **2.7.6** **Don't expect:** a confirmation modal before saving. **Don't expect:** other fields to be marked as edited.

### 2.8 Re-read the document ("Read again")

- [ ] **2.8.1** Click **"Read again"** in the header. **Expect:** a small spinning refresh icon on the button and toast **"Reading the file again…"**.
- [ ] **2.8.2** The Reading badge flips back to **"Waiting to read"** then **"Reading…"**, and the fields card shows "Reading the document…" again.
- [ ] **2.8.3** Wait for it to complete again. **Expect:** badge returns to **"Read"**, fields re-populated. **The manually-edited values you saved in 2.7 ARE overwritten** — re-reading replaces all fields from scratch by design (ADR 0017; the button's tooltip says "this replaces any edits you've made"). This is intended, not a bug.

### 2.9 Sign out and sign back in

- [ ] **2.9.1** Click **"Log out"** in the sidebar footer. **Expect:** redirect to `/login` (no confirmation dialog). **Don't expect:** any toast or "you've been signed out" message.
- [ ] **2.9.2** **Expect:** `/login` shows **"Welcome back"** ("Sign in to your CompliDrop workspace.") with email + password fields, a **"Sign in"** button, and a **"Forgot your password?"** link.
- [ ] **2.9.3** Type the same email + password from 2.3.1. Submit. **Expect:** button → **"Signing in…"**, then redirect to `/dashboard`, toast **"Welcome back!"**.
- [ ] **2.9.4** Dashboard now shows **"Total documents: 1"**, and the **"Recent activity"** card shows humanized entries like **"Document uploaded"**, **"Vendor added"**, **"Signed in"** with localized timestamps. **Don't expect:** raw action codes ("document.uploaded") or the old "Document · Uploaded" middle-dot format.

### 2.10 First-time UX impressions to log as findings

Not pass/fail — write down anything that *felt* off:

- [ ] Did you ever feel "what do I do next?" — log as UX finding (deferrable).
- [ ] Was any copy ambiguous or jargony for a SMB user?
- [ ] Did the polling cadence feel jumpy or jittery?
- [ ] Were colors/icons consistent — did the same status badge ever look different in two places?

---

## §3 — Authentication & account flows

### 3.1 Login form validation

- [ ] **3.1.1** Log out. Land on `/login`. Submit empty. **Expect:** inline errors **"Enter a valid email"** + **"Password is required"**.
- [ ] **3.1.2** Type a valid email, empty password. **Expect:** only the password error remains.
- [ ] **3.1.3** Type a malformed email (`not@an@email`). **Expect:** "Enter a valid email".

### 3.2 Bad credentials & lockout

- [ ] **3.2.1** Type your QA Admin A email + wrong password (`qa-launch-2026X`). Submit. **Expect:** toast **"Invalid email or password."** Button returns to **"Sign in"**.
- [ ] **3.2.2** Repeat until you reach the **10th** failed attempt. **Expect:** still **"Invalid email or password."** up to that point. (Lockout threshold is 10 — [`AuthLockout.cs`](../../api/CompliDrop.Api/Auth/AuthLockout.cs).)
- [ ] **3.2.3** On the 10th fail the account locks. Try again. **Expect:** a toast like **"Too many sign-in attempts — your account is locked for about 15 more minutes. Reset your password to regain access now."** (the minutes count down; the response is HTTP 423). **Don't expect:** a wall-clock time or a raw "423".
- [ ] **3.2.4** Type the **correct** password now. Submit. **Expect:** still locked (same lockout message).
- [ ] **3.2.5** **Wait 15 minutes** for the first lock window (it doubles per further fail, capped at 24h). OR use the **"Reset your password"** escape hatch (§3.8) to regain access immediately. (You can also clear `LockedUntil` in the DB if testing offline.)
- [ ] **3.2.6** **Don't expect:** the error to reveal whether the email exists. Bad email AND wrong password both produce **"Invalid email or password."**

### 3.3 Rate limiting

The `auth-strict` policy is **5 requests / minute / IP** on login + register + the password/email/verify endpoints.

- [ ] **3.3.1** After being unlocked (or in a fresh browser), submit the login form rapidly 6 times in under a minute (any password). **Expect:** the 6th attempt's toast is **"Too many requests. Please try again later."** (code `rate_limit.exceeded`).
- [ ] **3.3.2** Look at DevTools → Network on the 6th request. Response status: 429.
- [ ] **3.3.3** Wait one minute. Try again. **Expect:** can submit.

### 3.4 Registration edge cases

- [ ] **3.4.1** Try to register the same email (`ruben+qaA@gmail.com`) again. **Expect:** toast **"An account with that email already exists."** **Don't expect:** a 500, or a duplicate org silently created.
- [ ] **3.4.2** Register with a valid new email + password but leave name and company at one character. **Expect:** zod errors block submission before the network call.
- [ ] **3.4.3** Submit register with `?plan=funky` in the URL. **Expect:** the form renders with the default heading **"Start dropping docs"** and no plan banner (unknown plan falls back to Free).

### 3.5 Session persistence

- [ ] **3.5.1** Log in as Admin A. Open a new tab to `/documents`. **Expect:** session persists, you land on documents (not redirected to `/login`). Cookies are httpOnly so you can't read them in JS, but in DevTools → Application → Cookies you should see `cd_session`, `cd_refresh`, and `cd_session_hint`.
- [ ] **3.5.2** Close the browser tab. Reopen. Visit `/dashboard`. **Expect:** still logged in.
- [ ] **3.5.3** **Don't expect:** the session cookie to leak into JS (`document.cookie` should NOT show `cd_session` — that's the httpOnly guarantee; only the non-sensitive `cd_session_hint` is readable).

### 3.6 Silent session refresh & expiry redirect (#182)

The session cookie expires after 15 minutes; the frontend silently refreshes via `cd_refresh` on a 401.

- [ ] **3.6.1** (Optional, time-intensive) Stay logged in for 20+ minutes without activity, then click any nav link. **Expect:** the page loads normally — the user sees nothing despite the session having expired. DevTools → Network shows a `POST /api/auth/refresh` before the user-initiated request.
- [ ] **3.6.2** (Faster) In DevTools → Application → Cookies, manually delete `cd_session` (keep `cd_refresh`). Click a nav link. **Expect:** silent refresh, page loads. **Don't expect:** a flash of `/login` or a toast.
- [ ] **3.6.3** Delete BOTH `cd_session` AND `cd_refresh`. Click a nav link. **Expect:** the refresh fails, and the app now **redirects you to `/login`** (the query client nulls the `useMe()` cache → the layout bounces you). **Don't expect:** to be stranded on a "Something went wrong" card — genuine expiry routes to login now (this used to be a known limitation; it's fixed).
- [ ] **3.6.4 Transient `/me` failure (#182).** Simulate a brief 5xx on `/api/auth/me` (e.g. stop the API for ~2s while on a dashboard page, then restart). **Expect:** you are **NOT** logged out for a transient server error — only a definitive 401 logs you out. **Don't expect:** a logout loop on a flaky network.

### 3.7 Logout behavior

- [ ] **3.7.1** Click "Log out". **Expect:** redirect to `/login` immediately. All three cookies cleared (verify in DevTools → Application).
- [ ] **3.7.2** Press the browser back button. **Expect:** the previous dashboard page momentarily loads from cache, then redirects to `/login` (auth gate via `useMe()`).
- [ ] **3.7.3** **Don't expect:** any logged-in data to still be visible after logout.

### 3.8 Forgot password → reset (this flow now EXISTS — #183)

- [ ] **3.8.1** On `/login`, click **"Forgot your password?"** → `/forgot-password` loads. **Expect:** heading **"Reset your password"**, subtitle **"Enter your email and we'll send you a link to set a new password."**, an Email field, and a **"Send reset link"** button.
- [ ] **3.8.2** Enter `ruben+qaA@gmail.com` and submit. **Expect:** the page switches to a confirmation card: **"Check your email"** / **"If that email is registered, we've sent a link to reset your password. It expires in 45 minutes."** + a **"Back to sign in"** link.
- [ ] **3.8.3 Anti-enumeration.** Submit again with an email that does NOT exist. **Expect:** the **same** "Check your email" confirmation (no info about whether the account exists). **Don't expect:** a different message or a toast that reveals existence.
- [ ] **3.8.4** Open the reset email (subject **"Reset your CompliDrop password"**). **Expect:** a **"Reset my password"** button (and a paste-able link) and the line "This link expires in 45 minutes." Click it → `/reset-password?token=…`.
- [ ] **3.8.5** **Expect:** heading **"Choose a new password"** with **"New password"** + **"Confirm new password"** fields. Validation: short password → "At least 12 characters"; no letter → "Include a letter"; no digit → "Include a digit"; mismatch → "Passwords don't match".
- [ ] **3.8.6** Set a valid new password, submit (**"Reset password"** → **"Resetting…"**). **Expect:** a success card **"Password reset" / "Taking you to sign in…"**, then auto-redirect to `/login` after ~1 second. **Don't expect:** a toast.
- [ ] **3.8.7** Sign in with the **new** password. **Expect:** success. (Then change it back if you want to keep §2's password.)
- [ ] **3.8.8** Open `/reset-password` with **no token** (strip `?token=`). **Expect:** **"Invalid reset link" / "This link is missing its token. Request a fresh one from the sign-in page."** + a **"Request a new link"** link. Re-using an already-spent or expired token → server error message **"This reset link is invalid or has expired. Request a new one."**

### 3.9 Email verification (#184)

- [ ] **3.9.1** From the dashboard email-verification banner (§2.4.2), click **"Resend email"**. **Expect:** toast **"Verification email sent."** (or a server message).
- [ ] **3.9.2** Open the verification email (subject **"Confirm your email for CompliDrop"**), click **"Confirm my email"** → `/verify-email?token=…`. **Expect:** a brief **"Confirming your email…"** spinner, then a green **"Email confirmed"** card with a **"Continue to dashboard"** button.
- [ ] **3.9.3** Return to the dashboard. **Expect:** the amber verification banner is **gone** (no reload needed).
- [ ] **3.9.4** Open `/verify-email` with no token → **"Invalid verification link"**. An expired/spent token → **"Couldn't confirm your email"** with the server reason and a **"Go to dashboard to resend"** action.

### 3.10 Change password / change email / delete account (Settings → §11)

These live on `/settings` (Security + Danger zone). Tested here because they're auth flows.

- [ ] **3.10.1 Change password.** `/settings` → **"Security"** card → **"Change password"**. Fields: **Current password / New password / Confirm new password**. Wrong current password → **"Your current password is incorrect."** A valid change → toast **"Your password has been updated."**
- [ ] **3.10.2 Change email (deferred-confirm).** **"Change email"** sub-form. Helper: **"We'll send a confirmation link to the new address — your email changes only once you click it."** Fields: **New email** + **Confirm with your password**. Submit (**"Send confirmation link"**). **Expect:** toast like **"Check your new email to confirm the change."** The email does NOT change until you click the link sent to the **new** address. **Don't expect:** the displayed email to change before confirmation.
- [ ] **3.10.3 Delete account (two-step).** **"Danger zone"** → **"Delete account"** (warning **"Permanently deletes your account and organization data. This can't be undone."**). Click **"Delete my account"** → an inline confirm appears asking you to **"Enter your password to confirm"**. **Expect:** it requires your **password** (not a typed "DELETE" phrase). Wrong password → **"Your password is incorrect."** A correct delete → toast **"Your account has been deleted."** then redirect to `/login`. **Do this on a throwaway account, not QA Admin A.**
- [ ] **3.10.4 Export my data.** **"Danger zone"** → **"Export your data"** → **"Export my data"**. **Expect:** toast **"Download started"** and a `complidrop-account-export.json` download. **Don't expect:** raw HTTP errors on failure — the bare-fetch path emits the generic fallback.

---

## §4 — Document management

Reuse the QA Admin A account. Should already have 1 document from §2.

### 4.1 Empty/non-empty states

- [ ] **4.1.1** Navigate to `/documents`. **Expect:** "1 total" in the header, the table shows your sample-coi.pdf row.
- [ ] **4.1.2** Column headers in order: **File / Type / Vendor / Extraction / Compliance / Expires / (no header for the delete action)**.

### 4.2 Single-file upload (the "add details" flow — #186)

- [ ] **4.2.1** Click anywhere in the dropzone (not drag). **Expect:** native file picker opens.
- [ ] **4.2.2** Select `happy-path/sample-license.pdf`. **Expect:** the file **stages** (the "Add details before uploading" card), it does NOT upload yet.
- [ ] **4.2.3** Pick a vendor (reuse "Mike's Electrical" or add a new one), set **Document type** to **"Business License"**, click **"Upload 1 file"**. **Expect:** toast **"Uploaded sample-license.pdf"**, row appears, badge transitions **Waiting to read → Reading… → Read** (or **"Needs your review"** if low confidence).
- [ ] **4.2.4** **Don't expect:** the page to lose its scroll position when the row updates. **Don't expect:** an upload to start before a vendor is selected (the button stays disabled with **"Choose a vendor to continue."**).

### 4.3 Multi-file upload

- [ ] **4.3.1** Select all three `happy-path/*.pdf` files at once (Ctrl+A in the picker). **Expect:** all three **stage together** in the one details card (each with a remove "✕"). Pick a vendor + type for the batch, click **"Upload 3 files"**.
- [ ] **4.3.2** **Expect:** three toasts in succession — **"Uploaded sample-coi.pdf"**, **"Uploaded sample-license.pdf"**, **"Uploaded sample-permit.pdf"** — and three new rows. They upload sequentially. If one fails mid-batch, only the un-sent files stay staged so you can retry just those.

### 4.4 File-type rejection

- [ ] **4.4.1** Try to drag `validation-edge/docx-disallowed.docx` onto the **dashboard** dropzone. **Expect:** it is **silently filtered** — it never stages, no toast (the dropzone's `accept` is PDF/JPEG/PNG, so the browser rejects it client-side). The file does NOT appear. **Note:** unlike the portal (§6.3), the dashboard shows no friendly rejection message — log as a UX finding if it bothers you, not a launch-blocker.
- [ ] **4.4.2** Drag `validation-edge/fake.pdf` (a text file renamed). **Expect:** it stages (extension passes the client filter); after you add a vendor and upload, the server rejects it by **magic bytes** with a toast — **"Only PDF, JPEG, PNG, and HEIC/HEIF files are supported."** (or **"File is too small to be valid."** for a tiny one). **Don't expect:** the server to accept it.
- [ ] **4.4.3** Drag `validation-edge/fake.jpg` (a PDF renamed). Same expectation: it may stage, and the server validates by magic bytes. (A real PDF-renamed-jpg is actually a valid PDF, so it may be accepted as a PDF — the point is magic bytes win over the extension.)

### 4.5 File-size rejection

- [ ] **4.5.1** Upload `validation-edge/exactly-10mb.pdf`. **Expect:** accepted (10485760 bytes = exactly the limit). It will likely fail extraction (padding), ending at **"Couldn't read"** or **"Needs your review"** — fine; this tests size acceptance.
- [ ] **4.5.2** Try `validation-edge/over-10mb.pdf` (10485761+ bytes). **Expect:** the dashboard dropzone's `maxSize` filters it client-side (silently — no stage); if it somehow reaches the server it's rejected with **"File exceeds the 10 MB limit."** **Don't expect:** a hang or "Bad Gateway".
- [ ] **4.5.3** Upload `validation-edge/empty.pdf` (0 bytes). **Expect:** server rejects with **"Upload a PDF, JPEG, or PNG file."**

### 4.6 Image uploads

- [ ] **4.6.1** Upload `validation-edge/jpeg-real.jpg`. **Expect:** accepted. Extraction may produce few/no fields if it's not a real compliance doc — that's OK.
- [ ] **4.6.2** Upload `validation-edge/png-real.png`. Same expectation.
- [ ] **4.6.3** Upload `extraction-edge/photo-of-coi.jpg`. **Expect:** accepted. Extraction may land in **"Needs your review"** (low confidence) — that's the OCR-robustness test.
- [ ] **4.6.4 HEIC on the dashboard (known asymmetry — #220).** Try to upload `extraction-edge/sample-photo.heic` via the **dashboard** dropzone. **Expect:** it is **silently filtered** and does NOT stage — the dashboard dropzone's `accept` map is PDF/JPEG/PNG only and was not widened for HEIC. **HEIC is accepted end-to-end only via the vendor portal** (§6). This dashboard/portal mismatch is worth flagging as a follow-up (the API transcodes HEIC fine; only the dashboard dropzone gate is narrow). Log it as a finding, not a launch-blocker.

### 4.7 Detail page (incl. "why not compliant?" — #193)

Pick a fully-read document.

- [ ] **4.7.1** Click the filename. **Expect:** detail page (`/documents/{guid}`) within ~1s.
- [ ] **4.7.2** Click **"All documents"** (top-left, with a left-arrow). **Expect:** back to `/documents`.
- [ ] **4.7.3** Click **"View file"**. **Expect:** the original blob opens in a new tab. PDF renders; JPEG/PNG renders. (A HEIC uploaded via the portal will have been transcoded to JPEG, so it renders as an image.)
- [ ] **4.7.4** Change the **Type** select in the header. **Expect:** toast **"Document type updated"**.
- [ ] **4.7.5 ManualRequired CTA (#193).** If a doc landed in **"Needs your review"**, the detail page shows an amber card **"Please double-check these details"** prompting you to review the amber-bordered fields and click **Save changes**. **Do:** review and click **Save changes** (it's enabled even with zero edits in this state) — the doc flips to **"Read"**.
- [ ] **4.7.6 Why-not-compliant card (#193).** Once a vendor has a checklist (§7) and a doc fails it, the detail page shows a rose card **"Why isn't this compliant?"** listing each failed requirement in plain English (e.g. "General liability is below the $1,000,000 you require — this document shows $500,000."), an **"Email {vendor} to fix this"** button (opens a prefilled `mailto:`), and a count of other requirements met. **Don't expect:** raw field codes or operators in the reasons.
- [ ] **4.7.7 Failed-extraction card (#193).** A doc in **"Couldn't read"** shows a rose card **"We couldn't read this document"** with a friendly reason (e.g. "We tried several times but couldn't read this file. It may be blurry, password-protected…"), a **"Contact support"** link, and a collapsed **"Details for support"** disclosure holding the raw error. **Don't expect:** the raw error code in the headline.

### 4.8 Document not found

- [ ] **4.8.1** Manually navigate to `/documents/00000000-0000-0000-0000-000000000000`. **Expect:** **"Document not found."** with a sky link **"Back to documents"**.
- [ ] **4.8.2** **Don't expect:** a 500, a stack trace, or the 5xx error card (that's for real server errors — it reads "Couldn't load document." with a **Retry**).

### 4.9 Delete document (styled confirm now — #189)

- [ ] **4.9.1** Hover the trash-can icon on a row. **Expect:** ghost button highlight (`aria-label="Remove {filename}"`).
- [ ] **4.9.2** Click it. **Expect:** a **styled confirm dialog** (NOT a native browser `confirm()`): title **"Remove {filename}?"**, body **"This removes the document from your records and can't be undone."**, a rose **"Remove"** button + **"Cancel"**.
- [ ] **4.9.3** Click Cancel. **Expect:** no change.
- [ ] **4.9.4** Click the trash again, then **"Remove"**. **Expect:** toast **"Document removed"**, row disappears.
- [ ] **4.9.5** **Don't expect:** a permanent deletion (soft-delete via `DeletedAt`). Re-uploading the same filename yields a new row, not a "duplicate" warning. **Note:** the detail page has NO delete button — deletion is list-only.

### 4.10 Pagination, filters & search (#187)

- [ ] **4.10.1 Search.** In the search box (placeholder **"Search by file or vendor name…"**), type part of a filename. **Expect:** the list filters (debounced) and resets to page 1.
- [ ] **4.10.2 Status filter.** The **"Filter by compliance status"** select offers **All statuses / Compliant / Action needed / Expiring soon / Expired / Awaiting review**. Pick one. **Expect:** the list narrows.
- [ ] **4.10.3 Type filter.** **"Filter by document type"** offers **All types** + the six types (Certificate of Insurance / Business License / Permit / Certification / Contract / Other).
- [ ] **4.10.4 Expiry filter.** **"Filter by expiry"** offers **Any expiry / Expiring in 30 days / 60 days / 90 days**.
- [ ] **4.10.5 Clear.** When any filter/search is active, a **"Clear"** button appears and resets everything. **Don't expect:** a vendor filter dropdown — there isn't one in the UI.
- [ ] **4.10.6 Empty results.** Apply a filter that matches nothing. **Expect:** **"No documents match your filters."** (distinct from the zero-docs empty state in §2.5.3).
- [ ] **4.10.7 Pagination.** With > 25 documents, **expect** a footer **"Page 1 of N · {count} documents"** and **"Prev" / "Next"** buttons (Prev disabled on page 1, Next disabled on the last page). Page size is 25.

### 4.11 Assign a vendor to an existing document (#186 follow-on)

- [ ] **4.11.1** If any row shows `—` in the Vendor column (e.g. a legacy doc), **expect** an inline **"Assign vendor"** button on that row. Click it → a vendor picker appears. Select a vendor. **Expect:** toast **"Assigned to {vendor name}"** and the row updates. **Note:** the detail page lets you edit the **type** but not the vendor — vendor assignment is on the list row.

### 4.12 Polling (live updates without refresh)

- [ ] **4.12.1** Open `/documents` in two tabs (both Admin A). Upload a new PDF in tab 1.
- [ ] **4.12.2** Switch to tab 2. **Expect:** within ~5 seconds the new row appears without manual refresh.
- [ ] **4.12.3** Watch the badge in tab 2. **Expect:** it transitions Waiting to read → Reading… → Read / Needs your review with no refresh.
- [ ] **4.12.4** **Don't expect:** polling to continue forever after a terminal state — traffic should quiet down within ~10s.

### 4.13 Stale-data banner (intentional 5xx)

- [ ] **4.13.1** Stop the API server (`Ctrl+C` the `dotnet watch run` terminal). Wait 5s.
- [ ] **4.13.2** Trigger a refetch (navigate or wait for polling). **Expect:** an amber StaleDataBanner above the table — **"Couldn't refresh documents"** + a subline with the error message (or generic fallback) + a **"Try again"** button. The table still shows cached data. **Don't expect:** raw "Bad Gateway"/"TypeError" in the subline.
- [ ] **4.13.3** Restart the API. Click **"Try again"**. **Expect:** rotating icon, then banner disappears, data refreshes.

### 4.14 Error state (no cached data)

- [ ] **4.14.1** Log out, stop the API, log back in, visit `/documents` with no cache. **Expect:** a full alert card (rose) — **"Couldn't load documents."** + the server message + a **"Retry"** button (`role="alert"`). **Don't expect:** a genuine session-expiry to land here — that redirects to `/login`.
- [ ] **4.14.2** Restart API, click Retry. **Expect:** data loads.

### 4.15 Status-badge contract (humanized labels + icons — #188/#189)

Every badge now pairs a **leading icon** with a **humanized label** (color is never the sole signal). Verify by uploading varied files:

**Extraction badge (the "have we read it" machine state):**
- [ ] **4.15.1 Waiting to read** — slate, hourglass icon. No animation.
- [ ] **4.15.2 Reading…** — sky, refresh icon, `motion-safe` pulse (no pulse if you've enabled reduced motion).
- [ ] **4.15.3 Read** — emerald, check icon. (No confidence % anywhere — that copy is gone.)
- [ ] **4.15.4 Needs your review** — amber, warning-triangle icon. (Best triggered by `extraction-edge/low-confidence.pdf` or a phone photo.)
- [ ] **4.15.5 Couldn't read** — rose, file-x icon. (Trigger via `validation-edge/tiny.pdf` or a doc the LLM can't parse.)

**Compliance badge:**
- [ ] **4.15.6 Awaiting review** — slate, dashed-circle icon (no checklist to evaluate against yet).
- [ ] **4.15.7 Compliant** — emerald, check.
- [ ] **4.15.8 Action needed** — rose, warning-triangle (this is the humanized "NonCompliant").
- [ ] **4.15.9 Expiring soon** — amber, clock.
- [ ] **4.15.10 Expired** — rose, x-circle.

### 4.16 Compliance-status badge (preview, full test in §7)

- [ ] **4.16.1** A freshly uploaded document whose **vendor has no requirements checklist** shows compliance **"Awaiting review"** (slate) indefinitely. (Since uploads now require a vendor, every doc has one — but the vendor may have no checklist.)

---

## §5 — Vendors

### 5.1 Empty vendors page

- [ ] **5.1.1** Navigate to `/vendors`. **Expect:** heading "Vendors", subtitle **"Manage your vendors and their compliance documents."**, an add-vendor card at top, a dismissible tip **"Start with a vendor"**, and (if you've added none) empty state **"No vendors yet — add your first one above to start tracking their COIs and licenses."**. Table columns: **Vendor / Requirements / Docs / Active links / (manage)**.

### 5.2 Add vendor form

- [ ] **5.2.1** Try **"Add vendor"** with the Name field empty. **Expect:** the button is disabled (email is optional, name is required).
- [ ] **5.2.2** Type "ACME Plumbing" in **Name** (placeholder "Acme Catering"), leave **Contact email** empty (placeholder "ops@acmecatering.com"). **Expect:** button enables.
- [ ] **5.2.3** Click **"Add vendor"**. **Expect:** toast **"Vendor added"**, inputs clear, row appears.
- [ ] **5.2.4** Add another with an email (`ruben+vendor@gmail.com`). **Expect:** the email shows under the name in the table.

### 5.3 Vendor detail / edit

- [ ] **5.3.1** Click a vendor's name (or **"Manage"**) → detail page `/vendors/{guid}`.
- [ ] **5.3.2** **Expect:** form with **Name / Contact email / Contact phone / Category** and a **"What this vendor must prove"** dropdown.
- [ ] **5.3.3** The **"What this vendor must prove"** dropdown's first option is **"— No requirements set —"**, followed by the seeded **suggested checklists** (**Caterer**, **Event Rental Company**, **Security Service**, **Transportation / Shuttle**, **Photographer / Videographer**) plus any checklists you've created. Helper: **"Pick the checklist for their type — we check every document against it."** With **"— No requirements set —"** selected, an amber note warns: **"No requirements set — this vendor's documents won't be marked covered or not until you choose one."** **Don't expect:** an empty dropdown — if NO checklists appear at all, log a finding (system templates failed to seed).
- [ ] **5.3.4** Update phone/category. Click **"Save changes"**. **Expect:** toast **"Vendor updated"**.
- [ ] **5.3.5** Set the dropdown to one of the suggested checklists. Save. **Expect:** toast, the selection persists.

### 5.4 Portal upload links (email/copy — #190)

There's no longer a standalone "Generate upload link" button — link creation is folded into the Email/Copy actions.

- [ ] **5.4.1** Scroll to the **"Portal upload links"** card (subtitle "Share a link with {vendor} — they upload with no login."). If none exist: **"No links yet — generate one above."**
- [ ] **5.4.2** Save a **Contact email** on the vendor first. Then click **"Email link to {vendor}"**. **Expect:** a link is minted and emailed; toast **"Upload link emailed to {email}"**. **Don't expect:** the button to be enabled without a saved contact email (its tooltip says "Add a contact email above to email the link.").
- [ ] **5.4.3** Click **"Copy link"**. **Expect:** a link is minted/resolved and copied; toast **"Link copied — now paste it into an email to {vendor}."**
- [ ] **5.4.4** **Expect:** a list item appears — a read-only monospace URL input, a copy-icon button (`aria-label="Copy upload link"`), an emerald **"active"** badge, and **"0/20 uploads"**.
- [ ] **5.4.5** Paste the URL somewhere to verify it's real: `http://localhost:3000/portal/{32-char-token}` (or staging URL).
- [ ] **5.4.6** Click the small copy-icon button. **Expect:** toast **"Copied"**.
- [ ] **5.4.7** Click the **✕** (`aria-label="Revoke link"`) on a link. **Expect:** the link disappears immediately — **no confirmation dialog and no toast**. This matches the project's deliberate no-confirm choice for revokes (§16); verify it's the intended UX.

### 5.5 Vendor with documents

- [ ] **5.5.1** On `/documents`, confirm docs you uploaded show their vendor in the Vendor column (uploads require a vendor now, so this should be populated). Legacy `—` rows can be fixed via the **"Assign vendor"** row button (§4.11).
- [ ] **5.5.2** Back on `/vendors`, the **Docs** column should reflect each vendor's document count. After §6 portal uploads this should increase.
- [ ] **5.5.3** The **Active links** column shows a green **"{n} active"** badge (or "None").

### 5.6 Vendor delete (known gap)

- [ ] **5.6.1** **Note:** there is still **no delete-vendor button** in the UI. The API endpoint (`DELETE /api/vendors/{id}`) and a `useDeleteVendor` hook exist but aren't wired to any control. Confirm it's absent; log/keep as a known gap (§16), not a bug.

---

## §6 — Vendor portal (external persona)

This is the critical viral-loop UX. Test on actual mobile.

**Browser B** (different profile than Browser A) or **mobile**. NOT logged in.

### 6.1 Open the portal link

- [ ] **6.1.1** Copy a portal link from §5.4. Paste into Browser B. **Expect:** a brief **branded loading** state (a "Secure upload" chip + skeleton), then the page. Centered card, sky background.
- [ ] **6.1.2** Top: a sky chip **"Secure upload"** with a shield icon.
- [ ] **6.1.3** Heading: **"Hi {vendor name}"**.
- [ ] **6.1.4** Subtitle: **"{QA Admin A} asked for your latest compliance documents. Send them below."** (Note: "Send them below.", not the old "Drop them here.")
- [ ] **6.1.5 Owner instructions (#196).** If the owner set instructions for this link, a white card **"What {org} needs from you"** renders above the dropzone with their message (preserving line breaks). If none set, the card is absent.
- [ ] **6.1.6** Dropzone with cloud icon. On **mobile** the copy is **"Tap to choose a file or take a photo"**; on **desktop** it's **"Drag a file here or click to select"**. Helper: **"PDF, JPEG, or PNG · 10 MB max"**.
- [ ] **6.1.7** Counter line: **"0 / 20 uploads used on this link"**.
- [ ] **6.1.8** Footer: small grey **"Powered by CompliDrop"** (with "CompliDrop" emphasized).
- [ ] **6.1.9** **Don't expect:** any nav, sidebar, login/signup prompt, or "your CompliDrop dashboard" link. Single-purpose page.
- [ ] **6.1.10** **Don't expect:** the customer's email or org details beyond the org name (no leakage).

### 6.2 Vendor uploads happy path

- [ ] **6.2.1** Drop `happy-path/sample-coi.pdf`. **Expect:** "Uploading…", then a green **"Received"** card listing the file with **"Processing…"** beside it.
- [ ] **6.2.2** Counter updates to **"1 / 20 uploads used on this link"**.
- [ ] **6.2.3** Upload a second file. **Expect:** the Received card lists both; counter **"2 / 20"**.

### 6.3 Vendor file rejections (friendly, aria-live — #196)

- [ ] **6.3.1** Try `validation-edge/docx-disallowed.docx`. **Expect:** a rose, polite-announced error: **"We can't read that file type. Please upload a PDF or a photo (JPEG, PNG, or HEIC)."**
- [ ] **6.3.2** Try `validation-edge/over-10mb.pdf`. **Expect:** **"That file is over the 10 MB limit. If it's a photo, try taking it again from a bit further back, or upload a PDF."**
- [ ] **6.3.3** Try `validation-edge/empty.pdf`. **Expect:** **"That file is empty."**
- [ ] **6.3.4** Try dropping two files at once. **Expect:** **"Please drop one file at a time."**
- [ ] **6.3.5** Try `validation-edge/fake.pdf`. **Expect:** the server's magic-byte rejection surfaces in the same rose region.
- [ ] **6.3.6** **Don't expect:** any internal code ("document.unsupported_format") shown to the vendor. **Don't expect** the OLD "switch your iPhone to Most Compatible" copy — HEIC is accepted now, so that rejection is gone.

### 6.4 Customer side: verify uploads arrived

Switch to Browser A.

- [ ] **6.4.1** As Admin A, refresh `/documents`. **Expect:** new rows with the filenames the vendor uploaded; Vendor column shows the portal's vendor. (`UploadedBy` is recorded as the portal — not visible in the UI; just confirm the docs landed.)
- [ ] **6.4.2** Open one. **Expect:** detail page works, fields extract.
- [ ] **6.4.3** On `/vendors`, the vendor's **Docs** count reflects the new uploads.

### 6.5 Rate limit (per token)

The token allows **10 uploads/hour**. Slow but worth doing once.

- [ ] **6.5.1** From Browser B, upload 10 files rapidly. **Expect:** all 10 succeed.
- [ ] **6.5.2** Upload the 11th. **Expect:** a rose error (the server's **"Too many requests. Please try again later."**) PLUS a subline **"Try again in about an hour, or retry now."** AND a **"Retry upload"** button.
- [ ] **6.5.3** Click "Retry upload". **Expect:** still rate-limited.
- [ ] **6.5.4** Wait the window (or reset in the DB). Try again — should succeed.

### 6.6 Quota exhaustion (per link)

The link defaults to **20 uploads** max.

- [ ] **6.6.1** Upload until the counter reaches **"20 / 20"**. The 20th still succeeds.
- [ ] **6.6.2** Drop a 21st. **Expect:** a rose error **"Upload quota reached for this link."** + subline **"This link is exhausted. Please ask your customer to send you a new upload link."** — **no "Retry upload" button** (intentional). (A client-side guard may show the same quota message before the request even fires.)
- [ ] **6.6.3** After refreshing, the dropzone shows **"Upload limit reached on this link"** with reduced opacity and is disabled.
- [ ] **6.6.4** Customer side (Browser A): on the vendor's detail page, the link's badge has flipped from **"active"** to **"inactive"** and shows **"20/20 uploads"**.

### 6.7 Revoked / inactive link

- [ ] **6.7.1** Customer side: mint a fresh portal link for the same vendor (Copy link), then revoke it immediately (✕ in §5.4.7).
- [ ] **6.7.2** Open the revoked link in Browser B. **Expect:** a full-page **"This link is no longer available."** + **"Ask your customer for a fresh upload link."**
- [ ] **6.7.3** **Don't expect:** the vendor name or org name to leak on an invalid link.

### 6.8 Bogus token

- [ ] **6.8.1** Navigate to `/portal/this-is-not-a-real-token`. **Expect:** the same "This link is no longer available." page.

### 6.9 Mobile portal page + camera capture (#196) + HEIC (#220)

Use a real phone, not just DevTools emulation.

- [ ] **6.9.1** Open a fresh portal link on the phone. **Expect:** loads in under ~2 seconds even on 4G, with the branded loading state first.
- [ ] **6.9.2** The dropzone is large and tappable; the copy reads **"Tap to choose a file or take a photo"**; the counter is legible.
- [ ] **6.9.3** Tap the dropzone. **Expect:** the OS sheet offers **Take Photo / Photo Library / Choose File** (the input is `accept="image/*,application/pdf"`, which surfaces the camera). Take a new photo OR pick from the camera roll. **Expect:** upload proceeds ("Uploading…" → "Received").
- [ ] **6.9.4 HEIC end-to-end (#220).** On an iPhone with default camera settings, take/pick a **HEIC** photo (or upload `extraction-edge/sample-photo.heic`). **Expect:** it's **accepted** (the portal `accept` includes `.heic`/`.heif`), the server transcodes it to JPEG on ingest, and it extracts like any image. **Don't expect:** any "switch to Most Compatible / change your iPhone setting" rejection — that's gone.
- [ ] **6.9.5** **Don't expect:** a horizontal scroll, broken layout, or the keyboard pushing content off-screen.
- [ ] **6.9.6** **Don't expect:** an "Open in app" prompt or any reference to a CompliDrop mobile app (there isn't one).

### 6.10 Portal page should NOT poll

- [ ] **6.10.1** After uploading, watch DevTools → Network on the portal page. **Expect:** no continuous traffic. The vendor sees "Processing…" but the portal doesn't poll for status. **Don't expect:** repeated GETs every 3 seconds.

---

## §7 — Vendor requirements (checklists)

> **Rebuilt in #190/#192.** The page (route still `/rules`, nav label **"Vendor requirements"**) is now plain-English checklists — no field names, operators, or "Templates"/"system" jargon. Requirements are picked from a menu and shown as sentences.

### 7.1 The vendor-requirements page

- [ ] **7.1.1** Navigate to `/rules` (or click **"Vendor requirements"** in the sidebar). **Expect:** heading **"Vendor requirements"** + subhead **"Set what each kind of vendor must prove. We check every document you upload against the list automatically."** A two-column layout. A dismissible tip **"Build a checklist in plain English"**.
- [ ] **7.1.2** Left rail: a **"New checklist"** input (label **"New checklist (e.g. Caterer, DJ, Florist)"**, placeholder "Vendor type") + a **"+"** button (`aria-label="Create checklist"`, disabled until you type a name); a **"Your checklists"** section (empty state **"None yet — create one above, or start from a suggested checklist below."**); and a **"Suggested checklists"** section listing **Caterer**, **Event Rental Company**, **Security Service**, **Transportation / Shuttle**, **Photographer / Videographer**, each with a **"Use this"** button. **Don't expect:** a "Templates" heading, a "system" badge, or generic trade names.
- [ ] **7.1.3** With nothing selected, the main panel reads **"Pick a checklist on the left, or create one, to set what a vendor must prove."**

### 7.2 Create a checklist

- [ ] **7.2.1** Type "QA Caterer" + click **"+"**. **Expect:** toast **"Checklist created"**, a new entry under "Your checklists", auto-selected.
- [ ] **7.2.2** The main panel shows the checklist name as a heading + a **"Delete checklist"** button + an **"Add a requirement"** button. Empty body: **"No requirements yet."** + "Add the first one below — each is a plain-English sentence." **Don't expect:** any doc-type / field / operator / message columns.

### 7.3 Add requirements in plain English

- [ ] **7.3.1** Click **"Add a requirement"** → group **Insurance** → **"General liability — minimum coverage"**. Pick the **$1,000,000** preset (or type into the money field, placeholder "$1,000,000"). Click **Add**. **Expect:** a sentence row with a green check: **"Carries at least $1,000,000 in general liability insurance"**. **Don't expect:** to type "general_liability_limit", "min_value", or an unformatted number.
- [ ] **7.3.2** Click **"Add a requirement"** → group **Dates** → **"Document must not be expired"** → Add. **Expect:** **"Insurance has not expired"** with honest helper text (it confirms an expiration date is on file; no "future date" promise). A live **"A QA Caterer is compliant when … every document proves: …"** summary appears listing both requirements.
- [ ] **7.3.3** Explore the catalog: groups are **Insurance** (general/auto/professional/umbrella liability minimums, "Carries workers' compensation", "Names you as additional insured"), **Dates** ("Document must not be expired"), and **Licenses & permits** ("Has a license number on file", "Holds a specific license type", "License must not be expired"). **Don't expect:** the **Add** button to enable before a required value is entered — money shows the reason **"Enter a coverage amount"**, text shows e.g. **"Enter license type"**.

### 7.4 Edit / remove a requirement

- [ ] **7.4.1** Click the pencil (`aria-label="Edit requirement: Carries at least $1,000,000 in general liability insurance"`) on the general-liability row. **Expect:** the money field pre-fills as **"$1,000,000"**. Change to $2,000,000, click **Save**. **Expect:** the sentence updates in place (no duplicate row, **no toast** — edits save silently; only failures toast). **Note:** value-less requirements (workers' comp, "not expired", "license number on file") have **no pencil**, only a trash icon.
- [ ] **7.4.2** Click the trash (`aria-label="Remove requirement: …"`). **Expect:** the requirement disappears immediately — **no confirmation, no toast**.

### 7.5 Suggested checklists clone into an editable copy

- [ ] **7.5.1** Click **"Use this"** on a suggested checklist (e.g. "Caterer"). **Expect:** toast **"Checklist added — edit it to fit your vendors"**, a new editable copy appears under "Your checklists" and is auto-selected. The original suggestion stays under "Suggested checklists". (Selecting a *suggested* checklist directly shows a read-only preview with a "Use this" button instead of Delete.)

### 7.6 Apply a checklist to a vendor and watch compliance

- [ ] **7.6.1** Navigate to the vendor that owns the COIs you uploaded (`/vendors/{id}`). Set **"What this vendor must prove"** to "QA Caterer" (or "Caterer"). Save.
- [ ] **7.6.2** Open one of that vendor's documents. **Expect:** a compliance evaluation runs.
- [ ] **7.6.3** Trigger a fresh evaluation — **"Read again"**, or edit+save a field that feeds a rule (§2.7.5). **Expect:** within ~10s the **Compliance** badge resolves:
  - All requirements pass AND not expired → emerald **"Compliant"**.
  - Expiration date in the past → rose **"Expired"** (overrides others).
  - Expiration within 30 days (and otherwise passing) → amber **"Expiring soon"**.
  - A coverage/requirement miss → rose **"Action needed"**, and the detail page's **"Why isn't this compliant?"** card (§4.7.6) explains exactly which requirement failed.

### 7.7 Delete a checklist (styled confirm)

- [ ] **7.7.1** Select "QA Caterer" under "Your checklists". Click **"Delete checklist"**. **Expect:** a **styled confirm dialog** (NOT native `confirm()`): title **"Delete QA Caterer?"**, body **"This removes the checklist and all of its requirements. Vendors assigned to it will no longer be checked against it."**, a destructive **"Delete"** button.
- [ ] **7.7.2** Cancel. **Expect:** no change.
- [ ] **7.7.3** Delete it. **Expect:** toast **"Checklist removed"**, it disappears, and the panel resets to **"Pick a checklist on the left, or create one, to set what a vendor must prove."**

---

## §8 — Reminders

### 8.1 Default reminders seeded

- [ ] **8.1.1** Navigate to `/reminders`. **Expect:** heading "Reminders", subtitle **"Sent automatically at 8 AM in your org's local time zone."**
- [ ] **8.1.2** **Expect:** a config table with columns **Lead time / Notify team / Notify vendor / Active** and 4 rows: **"60 days before"**, **"30 days before"**, **"14 days before"**, **"7 days before"**. Toggles render as switches (sky when on, slate when off).
- [ ] **8.1.3** Verify the seeded defaults: all four **Active** ON, all four **Notify team** ON. **Notify vendor** is **OFF for the 60-day** row and **ON for 30 / 14 / 7**. (Confirmed against the registration seed.)

### 8.2 Toggle behavior (no toast — #189 switch a11y)

- [ ] **8.2.1** Toggle off the 60-day reminder. **Expect:** the switch flips. **No success toast.** Refresh — the change persists.
- [ ] **8.2.2** Inspect a toggle: it's a `role="switch"` with an `aria-label` like **"Notify vendor 30 days before expiry"**. **Note:** the silent save (no toast) is intentional but can feel "did it work?" — log as a UX finding if it bothers you. Only a **failure** toasts (global error toast).

### 8.3 Recent deliveries

- [ ] **8.3.1** Below the config table, a **"Recent deliveries"** card. With no expirations yet: **"No reminders sent yet."**
- [ ] **8.3.2** **Force a reminder send** (for testing). Options:
  - **Easiest:** set a test document's `ExpirationDate` in the DB to today + 7 days (matching an active reminder), then wait for the next 08:00 in your org's local TZ.
  - **Practical for QA:** insert a fake `ReminderLog` row in the DB and confirm the page renders it.
- [ ] **8.3.3** Once a reminder fires, **expect** a table with **When / Recipient / Status**. Status badge (humanized):
  - **"Delivered"** — emerald
  - **"Bounced — bad address"** / **"Marked as spam"** / **"Couldn't send"** — rose
  - **"Sent"** / **"Opened"** / **"Clicked"** — sky

### 8.4 Email content (when you can capture one)

- [ ] **8.4.1** Subject: **"[QA Admin A] sample-coi.pdf expires in 7 days"** (org / filename / matching lead time).
- [ ] **8.4.2** Body opens with a sky heading **"Compliance reminder"**.
- [ ] **8.4.3** Body text: **"Hi there,"** then **"Your document {filename} from {vendorName or 'a vendor'} expires on {Month D, YYYY} — that's {N} days from today."**
- [ ] **8.4.4** CTA line (plain text, **no button/link**): **"Log in to QA Admin A on CompliDrop to review and upload the renewal."**
- [ ] **8.4.5** Footer: small grey **"Sent automatically by CompliDrop. You can adjust reminder cadence in Settings → Reminders."**
- [ ] **8.4.6** **Don't expect:** a tracking pixel or unsubscribe link (transactional). **Don't expect:** raw HTML showing in the inbox client.

### 8.5 Resend dashboard verification

- [ ] **8.5.1** Open the Resend dashboard. Verify the email shows with status **delivered** (or sent → delivered within a minute).
- [ ] **8.5.2** Wait 30+ seconds, refresh `/reminders`. **Expect:** the recent-deliveries row shows **"Delivered"** (the Resend webhook updated it).

### 8.6 Email verification & reminder reliability (#184)

- [ ] **8.6.1** While Admin A's email is unverified, the dashboard shows the amber **"Confirm your email …"** banner (§2.4.2). **Note:** verifying the email (§3.9) is what ensures team reminders actually reach you. Confirm the banner clears after verification; treat reminder delivery to an unverified address as best-effort.

---

## §9 — Export

### 9.1 PDF audit report

- [ ] **9.1.1** Navigate to `/export`. **Expect:** heading "Export", subtitle **"Download audit-ready reports and raw data."**, two cards (PDF + CSV).
- [ ] **9.1.2** PDF card: header **"PDF audit report"**, description **"A formatted PDF covering all active documents plus the audit log for the date range you pick. Good to forward to an insurer or compliance officer."**, two date inputs (**From**, **To**) — From defaults to 30 days ago, To to today.
- [ ] **9.1.3** **Scope note (#197):** the PDF card shows **"The date range filters the activity log only — the documents table always lists all of your active documents."**
- [ ] **9.1.4** Click **"Download PDF"**. **Expect:** the button disables briefly (no spinner/skeleton — disable only), toast **"Download started"**, and a PDF download.
- [ ] **9.1.5** Open the file (filename like `complidrop-audit-2026-05-06-2026-06-05.pdf`). **Expect:**
  - Header: **"CompliDrop Audit Report"** + your org name + **"Generated {Month D, YYYY}"**.
  - **Documents** table: every active doc — columns **File / Vendor / Type / Expires / Compliance** (humanized labels).
  - **Audit Log** table: columns **When / Action / Entity / User**. The **User** column shows a human **actor name** (full name, or email, or **"System"** for system events) — not a raw id (#197).
  - A scope line: **"{N} events from {from} to {to}"**, or, if the log was capped, **"Showing the 500 most recent events from {from} to {to}"** (#197 cap disclosure — 500 max).
  - Footer: **"CompliDrop · QA Admin A"**.
- [ ] **9.1.6** **Don't expect:** any other org's documents, any soft-deleted documents, or PII you didn't enter.

### 9.2 Date range edge cases

- [ ] **9.2.1** Set From = today + 30 days (future). Download. **Expect:** a PDF with an empty/near-empty audit log — no error, no 500.
- [ ] **9.2.2** Set From = 2 years ago. Download. **Expect:** works; includes everything in range (audit log still capped at 500 with the disclosure line).
- [ ] **9.2.3** Set From > To. **Expect:** either a validation message OR a graceful empty result. Log whichever you see.

### 9.3 CSV export

- [ ] **9.3.1** CSV card: header **"CSV export"**, description **"All active documents as CSV — useful for spreadsheets, BI tools, or one-off reporting."** (no date inputs — CSV always exports all active docs).
- [ ] **9.3.2** Click **"Download CSV"**. **Expect:** toast "Download started", file `complidrop-documents-2026-06-05.csv` (uses the To-date).
- [ ] **9.3.3** Open it. **Expect:** header row **Id, FileName, Vendor, Type, Status, Compliance, EffectiveDate, ExpirationDate, GeneralLiabilityLimit, UploadedBy, CreatedAt** (Type/Status/Compliance humanized).
- [ ] **9.3.4** **Don't expect:** soft-deleted documents, or another org's data.

### 9.4 Single-vendor PDF (KNOWN GAP)

- [ ] **9.4.1** The endpoint `GET /api/export/vendor/{id}` exists (PDF header "Vendor Compliance Package") but is **NOT** surfaced in the UI — nothing on `/export` links to it. Confirmed still a gap (§16). **Don't test it as a user.** Decide pre-launch whether to surface it.

### 9.5 Error handling

- [ ] **9.5.1** Stop the API. Click **"Download PDF"**. **Expect:** toast **"Something went wrong. Try again."** **Don't expect:** "Bad Gateway", "Failed to fetch", or `(502)` (the bare-fetch blob path collapses every error to the generic fallback).
- [ ] **9.5.2** Restart API. Retry — works.

---

## §10 — Billing

**Stripe test mode confirmed?** If not, stop and switch.

### 10.1 Settings billing tiles (free user)

- [ ] **10.1.1** Navigate to `/settings`. **Expect:** an **Organization** card (see §11) + a **"Plan & billing"** card showing:
  - Sub-copy: **"You're on the Free plan."** (no emerald "paid" badge — that's paid-only; billing status, if any, shows as a separate colored banner, not "· active").
  - A 3-cell mini stat block: **Documents** "{N} / 5", **Vendor portal** "Off", **AI reading cost** "$0.00" with caption **"this month · included in your plan"**. **Don't expect:** the old "LLM SPEND MTD" label.
  - Three upgrade tiles: **Pro** ($49/mo, "Unlimited docs, all features."), **Annual** ($39/mo, "Same features, billed yearly.", featured/sky-bordered, billed-note "Billed $468/year · save $120"), **Founding** ($39/mo, "Locked forever. First 50 only."). Each button reads **"Upgrade to Pro/Annual/Founding"**.
- [ ] **10.1.2** **Don't expect:** a "Manage billing" button (paid-only).

### 10.2 Plan limit enforcement

- [ ] **10.2.1** Get to 5 documents (the free limit shown as "{N} / 5").
- [ ] **10.2.2** Try to upload a 6th (remember: stage → pick vendor → Upload). **Expect:** toast **"Document limit of 5 reached. Upgrade to add more."** The row does NOT appear.
- [ ] **10.2.3** **Don't expect:** the upload to silently succeed then fail at extraction — the rejection is at upload time.

### 10.3 Stripe Checkout — Pro

- [ ] **10.3.1** Click **"Upgrade to Pro"**. **Expect:** button → **"Redirecting…"**, then navigation to Stripe Checkout (`checkout.stripe.com`).
- [ ] **10.3.2** Stripe page shows CompliDrop branding, product "CompliDrop Pro" or similar, price $49/month.
- [ ] **10.3.3** **Don't expect:** any "monthly" plan id (legacy vocab — backend rejects "monthly").
- [ ] **10.3.4** Email field pre-filled with your QA Admin A email.
- [ ] **10.3.5** Card `4242 4242 4242 4242`, any future expiry, any CVC, any zip.
- [ ] **10.3.6** Click "Subscribe". **Expect:** Stripe processes, then redirects to `http://localhost:3000/settings?upgraded=true`.
- [ ] **10.3.7** On return: toast **"Welcome — you're now on a paid plan!"**
- [ ] **10.3.8** **Expect (within ~5s):** the card now reads **"You're on the Pro plan."** + an emerald **"paid"** badge. The Documents stat drops the "/5". Vendor portal shows "On". The three tiles are replaced by a single **"Manage billing"** button.
- [ ] **10.3.9** **Don't expect:** an instant update if the webhook lags — there can be a 2–5s delay. If after 30s it still shows Free, force-refresh; if still Free, the webhook didn't fire — launch-blocker.

### 10.4 Receipt email (from Stripe)

- [ ] **10.4.1** Check the inbox. **Expect:** within a minute, a Stripe receipt email.
- [ ] **10.4.2** **Don't expect:** a CompliDrop-sent "welcome to paid" email (none wired in the MVP).

### 10.5 Free document limit no longer applies

- [ ] **10.5.1** Upload past 5 total. **Expect:** uploads continue; no "Document limit" toast.

### 10.6 Customer portal

- [ ] **10.6.1** Click **"Manage billing"**. **Expect:** button → "Redirecting…", navigation to the Stripe customer portal.
- [ ] **10.6.2** The portal shows the active subscription, payment method, billing history.
- [ ] **10.6.3** Change the card to `4000 0025 0000 3155`, complete the 3DS challenge. **Expect:** new default card set.
- [ ] **10.6.4** Test "Cancel plan". **Expect:** Stripe confirms, sets cancel-at-period-end, returns you toward `/settings`.
- [ ] **10.6.5** Back on `/settings`. **Expect:** still Pro/active for now (cancels at period end). After the period ends and `customer.subscription.deleted` fires, the user reverts to Free.
- [ ] **10.6.6** **Don't expect:** an in-app dunning banner before the cancellation actually happens.

### 10.7 Failed-payment flow (optional)

- [ ] **10.7.1** Use a test customer + card `4000 0000 0000 0341` (succeeds, fails on renewal). Fire a test `invoice.payment_failed` webhook.
- [ ] **10.7.2** **Expect:** subscription flips to `past_due`. The settings card sub-copy stays **"You're on the Pro plan."** and a separate **rose banner** appears: **"Your last payment didn't go through. Update your card to keep your account active."** (Note: status is a banner now, not a "· past_due" suffix.)
- [ ] **10.7.3** **Don't expect:** an in-app modal.

### 10.8 Cancel checkout (user backs out)

- [ ] **10.8.1** As a free user, click **"Upgrade to Annual"**. On the Stripe page, back out/close. **Expect:** redirect to `/settings?canceled=true` and an **info** toast **"Checkout canceled — no changes made."**
- [ ] **10.8.2** Settings still shows Free.

### 10.9 Founding plan

- [ ] **10.9.1** As a free user, click **"Upgrade to Founding"**. **Expect:** checkout works the same, $39/mo. After success the card reads **"You're on the Founding plan."** + emerald "paid".

### 10.10 Unknown plan rejection

- [ ] **10.10.1** Open DevTools console on `/settings`. Run:
  ```js
  fetch('/api/billing/checkout', {method:'POST', headers:{'Content-Type':'application/json', 'Idempotency-Key': crypto.randomUUID()}, credentials:'include', body:JSON.stringify({plan:'monthly'})})
  ```
  **Expect:** 400, code `billing.plan_unknown`, message **"Unknown plan. Expected one of: pro, annual, founding."** **Don't expect:** a Stripe session for an invalid plan.

### 10.11 Idempotency

- [ ] **10.11.1** Fire two checkouts with the same `Idempotency-Key`. **Expect:** both return the same `sessionUrl`. (Hard to do by hand — log as "verified by code only" if you skip.)

---

## §11 — Settings

### 11.1 Account / Organization card (now EDITABLE — #185)

- [ ] **11.1.1** **Expect:** an **"Organization"** card ("Your time zone controls when daily reminders are sent.") with an **editable** **Organization name** input and an **editable** **Time zone** select (IANA zones), a live preview line of when the next reminder send is, plus read-only **Email** and **Role** lines. **Don't expect:** the old "no edit button" behavior — org name + time zone are editable now.
- [ ] **11.1.2** Change the time zone, click **"Save changes"** (→ **"Saving…"**). **Expect:** toast **"Organization settings saved."** Blank the name and save → **"Organization name is required."**
- [ ] **11.1.3** The **Role** shows "admin"; **Email** shows your sign-in email. (Email changes go through §3.10.2's confirm flow, not this card.)

### 11.2 Product tour card (#191)

- [ ] **11.2.1** **Expect:** a **"Product tour"** card ("Replay the welcome walkthrough and bring back the first-visit tips.") with a **"Restart tour"** button. Click it → navigate to the dashboard → the welcome modal + page tips reappear.

### 11.3 Security & Danger zone

- [ ] **11.3.1** Confirm the **"Security"** card (change password / change email) and **"Danger zone"** (export data / delete account) render at the bottom. These flows are tested in §3.10.

### 11.4 Plan badge in sidebar

- [ ] **11.4.1** On Pro: sidebar plan badge shows **"Pro"**.
- [ ] **11.4.2** On Founding: **"Founding"**. On Free: **"Free"**. (Capitalized, sourced from `useMe()`.)

---

## §12 — Dashboard

### 12.1 KPI cards reflect real data

- [ ] **12.1.1** With several documents uploaded (so the stat grid is unhidden), verify the primary row labels + counts: **"Total documents"**, **"Compliant"**, **"Expiring ≤ 30d"**, **"Non-compliant"**.
- [ ] **12.1.2** Secondary row: **"Vendors tracked"**, **"Still being read"** (docs currently Waiting to read / Reading…), **"Compliance rate"** (compliant / total %). **Don't expect:** the old "Awaiting extraction" label — it's **"Still being read"** now.

### 12.2 Expiry pipeline

- [ ] **12.2.1** Heading **"When documents expire"** with 5 bars labeled **"Expired"**, **"Next 30 days"**, **"30–60 days"**, **"60–90 days"**, **"90+ days"** (en-dashes). Bars are proportional to the largest bucket. **Don't expect:** the old "0-30d / 30-60d" labels.
- [ ] **12.2.2** **Don't expect:** bars to overflow the card with large counts.

### 12.3 Recent activity

- [ ] **12.3.1** Up to 6 entries, each a humanized action — **"Document uploaded"**, **"Vendor added"**, **"Signed in"**, **"Requirement saved"**, **"Account created"**, etc. — plus a localized timestamp. **Don't expect:** raw codes ("document.uploaded") or the old "Document · Uploaded" middle-dot format.
- [ ] **12.3.2** **Don't expect:** null/undefined entries.

### 12.4 Dashboard handles slow data

- [ ] **12.4.1** Stop the API. Refresh `/dashboard`. **Expect:** stat cards degrade to zeros silently; the activity card shows a loading skeleton or empty. **Don't expect:** a hard crash or a "Bad Gateway" splash.
- [ ] **12.4.2** **Note:** the dashboard has **no** stale-data banner / retry button (unlike Documents/Vendors). Known gap (§16) — log as polish if it bothers you.

---

## §13 — Multi-tenancy isolation

The single most important security UX test. If this fails, **immediately stop and file launch-blocker**.

**Setup:** Browser B (different profile). Fresh, NOT logged in.

### 13.1 Register Admin B

- [ ] **13.1.1** In Browser B, `/register`. Register: Email `ruben+qaB@gmail.com`, Company "QA Admin B", Password `qa-launch-2026B`.
- [ ] **13.1.2** Land on `/dashboard`. **Expect:** the onboarding experience (welcome modal + get-started checklist), everything empty (0 documents, 0 vendors). **Don't expect:** any of Admin A's data.
- [ ] **13.1.3** **Critical:** sidebar org name is **"QA Admin B"**. **Don't expect:** any of Admin A's counts, vendors, documents, or activity.

### 13.2 Cross-org reads

- [ ] **13.2.1** Copy a document GUID from Admin A (Browser A → `/documents` → a row URL). In Browser B, navigate to `/documents/{that-guid}`.
- [ ] **13.2.2** **Expect:** **"Document not found."** with "Back to documents". **Don't expect:** the doc to load OR a 403 — it must look identical to a truly-nonexistent doc.
- [ ] **13.2.3** Same with a vendor GUID from Admin A → Admin B must see nothing/empty. Unreachable.

### 13.3 Cross-org writes

- [ ] **13.3.1** In Browser B's console:
  ```js
  fetch('/api/documents/{admin-A-doc-guid}/fields', {method:'PUT', headers:{'Content-Type':'application/json'}, credentials:'include', body:JSON.stringify({fields:[]})})
  ```
  **Expect:** 404 (the tenant filter pretends the doc doesn't exist). **Don't expect:** 200, 403, or any change to A's data.

### 13.4 Vendor portal token tenant scope

- [ ] **13.4.1** As Admin B, create a vendor + mint a portal link (Copy link). Note the token.
- [ ] **13.4.2** Open B's portal link, upload a file. **Expect:** the doc appears in **B's** `/documents`, NOT A's. The portal heading shows B's org name.
- [ ] **13.4.3** In Browser A, refresh `/documents`. **Expect:** the file uploaded via B's portal is NOT visible.

### 13.5 Recent activity isolation

- [ ] **13.5.1** A's dashboard recent activity shows only A's events; B's shows only B's. **Don't expect:** any cross-bleed.

### 13.6 Export isolation

- [ ] **13.6.1** Download Admin A's PDF audit report. Verify it contains ONLY A's documents (and A's actor names).
- [ ] **13.6.2** Same for CSV.

### 13.7 Auth cookies

- [ ] **13.7.1** A's `cd_session` belongs to org A; B's to org B. Different profiles → not shared.

---

## §14 — Edge cases & error states

### 14.1 Long filenames

- [ ] **14.1.1** Upload a file named `this-is-an-absurdly-long-filename-that-should-not-break-the-layout-…-2026.pdf`. **Expect:** the table row truncates/wraps cleanly; the detail h1 wraps but doesn't overflow.

### 14.2 Special chars in filenames

- [ ] **14.2.1** Upload `résumé&special—chars (2026).pdf`. **Expect:** displays correctly (no `&amp;`, `%20`, or double-encoding). "View file" yields the same name.

### 14.3 Unicode in vendor name

- [ ] **14.3.1** Add a vendor "Müller Glas & Söhne 株式会社". **Expect:** renders correctly in the table, the document Vendor column, the portal heading ("Hi Müller Glas…"), and reminder emails.

### 14.4 Empty input edge cases

- [ ] **14.4.1** In the vendor edit form, blank the Name and save. **Expect:** a validation/server rejection. **Don't expect:** a vendor saved with an empty name. (The add-vendor button is already disabled when Name is empty.)
- [ ] **14.4.2** On `/rules`, the New-checklist "+" stays disabled for an empty/whitespace name; the Add-requirement button stays disabled until a required value is entered.

### 14.5 Concurrent edits

- [ ] **14.5.1** Open a document detail in two tabs (Admin A). Edit a field in tab 1, save. Edit the same field in tab 2 (different value), save.
- [ ] **14.5.2** **Expect:** last write wins (acceptable for MVP). Refresh tab 1 → the newer value. **Don't expect:** a 500 or corrupted state.

### 14.6 Browser back button after submit

- [ ] **14.6.1** Submit registration, land on `/dashboard`, hit back. **Expect:** `/register` (form may or may not be populated). **Don't expect:** a duplicate-account error if you re-submit (duplicate-email check handles it).

### 14.7 Tab close mid-upload

- [ ] **14.7.1** Start uploading a ~9 MB file; before the response, close the tab. **Expect:** no zombie state — reopen `/documents` and the doc is either present+processing or absent.

### 14.8 5xx brown-out

- [ ] **14.8.1** Hard-kill the API mid-session. Try to use the app.
- [ ] **14.8.2** **Expect:** every action shows the generic fallback toast or a stale-data banner. NEVER "Bad Gateway"/"TypeError". Console errors are OK as long as nothing user-visible leaks internals.
- [ ] **14.8.3** Restart the API. **Expect:** subsequent actions work; no "stuck" UI requiring a full refresh (refresh is OK as a worst case).

### 14.9 Polling during DB suspended (Neon cold start)

- [ ] **14.9.1** On a Neon-backed staging deploy, idle 6+ minutes (past Neon's suspend), then load `/documents`.
- [ ] **14.9.2** **Expect:** first request takes 2–5s (cold start); UI shows loading then data. **Don't expect:** a freeze, an error toast, or an empty state shown before data.

### 14.10 Very old expired documents

- [ ] **14.10.1** Give a doc an `ExpirationDate` in 2020 (edit the field after extraction). **Expect:** compliance **"Expired"** (rose). The list Expires column shows the date + a rose relative span like "{n}d ago".

### 14.11 Very far future expiration

- [ ] **14.11.1** A doc expiring in 2099. **Expect:** an "in {n}d" relative span — no crash, no NaN.

### 14.12 Friendly extraction-failure copy (#193)

- [ ] **14.12.1** Force a failed extraction (e.g. a corrupt/unreadable file, or hit the monthly cost ceiling). **Expect:** the doc lands in **"Couldn't read"** and the detail page shows **"We couldn't read this document"** with a plain-English reason and a **"Details for support"** disclosure for the raw code. **Don't expect:** the raw `extraction.*` code in the headline.

---

## §15 — Accessibility & polish

### 15.1 Keyboard navigation

- [ ] **15.1.1** On `/dashboard`, Tab repeatedly. **Expect:** the first focusable element is a **"Skip to content"** link; then focus moves visibly through sidebar links and main content, each with a visible focus ring.
- [ ] **15.1.2** Enter on a focused sidebar link navigates.
- [ ] **15.1.3** Tab to the documents dropzone, press Enter/Space. **Expect:** the file picker opens.

### 15.2 Label association (project rule)

- [ ] **15.2.1** On `/register`, click the literal text "Full name". **Expect:** focus moves to the input. Same for every field.
- [ ] **15.2.2** Same on `/login`, `/forgot-password`, `/reset-password`, `/settings` (org name, change-password), `/vendors/{id}`.
- [ ] **15.2.3** If clicking a label doesn't focus its control, that's a `jsx-a11y/label-has-associated-control` violation — launch-blocker.

### 15.3 Screen reader spot-check

NVDA on Windows is free.

- [ ] **15.3.1** On `/dashboard`, the heading announces as "Welcome, {name}" (h1); the sidebar nav announces as a navigation landmark ("Primary").
- [ ] **15.3.2** Force a stale-data banner (kill the API). **Expect:** announced as a status (`role="status"`, `aria-live="polite"`) — non-interrupting.
- [ ] **15.3.3** Force a full error card (5xx, no cache). **Expect:** announced as an alert (`role="alert"`).
- [ ] **15.3.4** On `/rules`, a requirement's trash icon announces as **"Remove requirement: {sentence}, button"** and the pencil as **"Edit requirement: {sentence}, button"** (aria-labels set).
- [ ] **15.3.5** The email-verification banner announces as a region (`role="region"`, "Confirm your email") — context, not an alert.

### 15.4 Color contrast & icon redundancy (#189)

- [ ] **15.4.1** Use DevTools → Accessibility to check contrast for sky buttons, slate badges, amber badges on white.
- [ ] **15.4.2** **Expect:** WCAG AA (4.5:1 normal, 3:1 large). Confirm every status badge pairs an **icon** with its label (color is never the sole signal). Log any failures as launch-blocker.

### 15.5 Browser zoom

- [ ] **15.5.1** Zoom to 150% on a dashboard page. **Expect:** layout stays usable, nothing overflows.
- [ ] **15.5.2** Zoom to 200%. **Expect:** still readable; nothing cut off.

### 15.6 Responsive / mobile (#181 — real mobile shell now)

Test on a real phone (or DevTools mobile emulation):

- [ ] **15.6.1** `/dashboard` on a phone viewport: the desktop sidebar is hidden and a sticky top bar shows a hamburger (`aria-label="Open navigation menu"`). Tap it → a left **drawer** (Sheet) opens with the full nav + footer; it has a close button (`aria-label="Close navigation menu"`), traps focus, closes on Escape / backdrop / link-tap. KPI cards stack vertically.
- [ ] **15.6.2** `/documents` on mobile: the table reflows to readable stacked rows (each cell carries its field label) or scrolls horizontally inside its container. The upload zone is tappable.
- [ ] **15.6.3** The marketing header (logged out) also collapses its nav into a mobile menu.
- [ ] **15.6.4** `/portal/{token}` on mobile (already tested in §6.9 — re-verify no horizontal scroll).
- [ ] **15.6.5** `/login`, `/register` on mobile: fields are properly sized; the submit button is reachable above the keyboard.
- [ ] **15.6.6** **Don't expect:** any horizontal scroll on the page body (only inside scrollable containers like tables).

### 15.7 Reduced motion (#189)

- [ ] **15.7.1** Enable "Reduce motion" in your OS. **Expect:** the **"Reading…"** badge pulse stops (it's `motion-safe`), drawer/modal transitions are muted (`motion-reduce:transition-none`), and skeleton shimmer is reduced. **Don't expect:** any essential information conveyed only by an animation.

### 15.8 Cross-browser

- [ ] **15.8.1** **Chrome** (primary).
- [ ] **15.8.2** **Firefox**: check the dropzone (react-dropzone) + the drawer.
- [ ] **15.8.3** **Safari** (if available): cookie + cross-origin; verify login/logout.
- [ ] **15.8.4** **Edge**: identical to Chrome, but test anyway.

### 15.9 Print stylesheet

- [ ] **15.9.1** On `/dashboard`, Ctrl+P. **Expect:** a printable representation (likely unstyled — OK for MVP). **Don't expect:** broken pages or the sidebar/buttons trying to print.

### 15.10 Visual consistency

- [ ] **15.10.1** Are all "Save changes" buttons consistent across forms?
- [ ] **15.10.2** Is the "active" badge style consistent (portal links, etc.)?
- [ ] **15.10.3** Do status badges look identical for the same status across list and detail?
- [ ] **15.10.4** Are card spacings/paddings consistent?

### 15.11 Favicon & OG image

- [ ] **15.11.1** The favicon (water droplet) shows on CompliDrop tabs. **Don't expect:** a default Next.js favicon.
- [ ] **15.11.2** Paste the landing URL into Slack/Twitter. **Expect:** the OpenGraph image renders.

### 15.12 Loading transition smoothness

- [ ] **15.12.1** Navigate rapidly between sidebar items. **Expect:** smooth, no flash of unstyled content; skeletons brief but visible.

---

## §16 — Known limitations (NOT bugs)

If you encounter any of these, DO NOT file as bugs — they're intentional MVP scope choices or documented post-launch follow-ups. Just confirm the behavior matches.

### 16.1 No upload progress percentage

Both the dashboard staging upload and the portal show only an indeterminate "Uploading…" — no progress bar/percentage. On a slow link a 9 MB file may look frozen. Post-launch polish ([#150](https://github.com/neboxdev/complidrop/issues/150)).

### 16.2 Cost ceiling discovery is reactive

If an org hits its monthly extraction cost ceiling ($5 free / $50 paid), there's no proactive in-app warning. The user discovers it when a doc lands in **"Couldn't read"** — the detail page now explains it in plain English ("…your account hit its monthly processing limit. It resumes next cycle…"). Adding an 80%-warning banner is a post-launch enhancement.

### 16.3 Single-vendor PDF export endpoint exists but has no UI

`GET /api/export/vendor/{id}` works (PDF "Vendor Compliance Package") but nothing on `/export` links to it. Decide pre-launch whether to surface it.

### 16.4 Dashboard upload doesn't accept HEIC (portal does)

The vendor **portal** accepts HEIC/HEIF photos and the API transcodes them to JPEG (#220). The **dashboard** dropzone's client `accept` map was not widened, so a `.heic` picked on the dashboard is silently filtered and won't stage. Owners on iPhones must use a JPEG/PDF on the dashboard, or upload via the portal. Worth surfacing pre-launch (small frontend change); until then it's an accepted gap.

### 16.5 Removing an individual requirement has no confirmation

On `/rules`, the per-requirement trash icon deletes immediately (no confirm, no undo). Note: **checklist** deletion DOES use a styled confirm dialog — only individual requirement rows are immediate. Intentional; flag only if QA finds it surprising.

### 16.6 Vendor portal-link revoke has no confirmation

The ✕ on a portal link revokes immediately, no confirm, no toast. Intentional.

### 16.7 Toggling reminders has no feedback

Flipping a reminder toggle saves silently — no toast/spinner (only a failure toasts). Intentional per spec; can feel ambiguous.

### 16.8 No delete-vendor button

The API can delete a vendor but no UI control is wired. A vendor can only be edited or left unused. Decide pre-launch whether to surface delete.

### 16.9 Dashboard has no stale-data banner

If `/dashboard`'s data fetch fails, the cards silently show zeros — no error card or retry. (Documents/Vendors pages DO have the banner; the dashboard doesn't.)

### 16.10 Export has no in-flight skeleton

`/export` only disables the button while a download is in flight — no spinner/skeleton. Minor polish.

### 16.11 Founding plan is auth-only

You won't see Founding on the landing page or via `?plan=founding`. It's only offered to logged-in users on `/settings`. Per [ADR 0011](../adr/0011-plan-vocab-unified-with-founding-as-authenticated-only-promo.md).

> **Resolved since the previous plan revision:** forgot-password now exists (§3.8); genuine session-expiry now auto-redirects to `/login` (§3.6.3); a transient `/me` 5xx no longer logs you out (§3.6.4); org name + time zone are now editable (§11.1); a vendor can be assigned to an existing document from the list row (§4.11). Don't re-file these as gaps.

---

## §17 — Performance & feel

Not formal load testing — just perceived responsiveness.

### 17.1 Time to interactive

- [ ] **17.1.1** Open `/` incognito. **Expect:** DOMContentLoaded < 2s on a good connection.
- [ ] **17.1.2** First click on a CTA responds within ~100 ms.

### 17.2 Dashboard cold load

- [ ] **17.2.1** Log in fresh (or after a Neon cold-start). **Expect:** initial render within 2s, stats populate shortly after.

### 17.3 Polling overhead

- [ ] **17.3.1** With one doc Reading…, watch Network. **Expect:** ~one request every 3s (detail) or every 5s (list).
- [ ] **17.3.2** **Don't expect:** continuous traffic after all docs reach a terminal state.

### 17.4 Many documents

- [ ] **17.4.1** Upload all 25 from `stress/25-mixed-docs/` (stage them — the picker accepts multi-select; pick one vendor for the batch). **Expect:** rows appear, the queue processes sequentially (~30–60s each). The list paginates at 25.
- [ ] **17.4.2** **Don't expect:** the page to slow noticeably; pagination caps the rendered rows.

### 17.5 Bundle size sanity

- [ ] **17.5.1** Reload the landing page, watch Network. **Expect:** total transfer under ~500 KB on first load. If > 2 MB, log a finding.

### 17.6 Memory leaks

- [ ] **17.6.1** Open `/documents` with several Reading… docs; let it poll 5+ minutes. **Expect:** memory stays stable in DevTools → Memory.

---

## §18 — Sign-off checklist

You're done when this entire table is filled in.

| Section | Status | Bugs found | Critical bugs? | Notes |
|---|---|---|---|---|
| §0 Setup | [ ] | | | |
| §1 Smoke | [ ] | | | |
| §2 First-time user | [ ] | | | |
| §3 Auth & account | [ ] | | | |
| §4 Documents | [ ] | | | |
| §5 Vendors | [ ] | | | |
| §6 Portal | [ ] | | | |
| §7 Vendor requirements | [ ] | | | |
| §8 Reminders | [ ] | | | |
| §9 Export | [ ] | | | |
| §10 Billing | [ ] | | | |
| §11 Settings | [ ] | | | |
| §12 Dashboard | [ ] | | | |
| §13 Multi-tenancy | [ ] | | | |
| §14 Edge cases | [ ] | | | |
| §15 Accessibility | [ ] | | | |
| §17 Perf/feel | [ ] | | | |

### Launch decision

- [ ] All `launch-blocker` bugs have shipped a fix.
- [ ] All `major` bugs have either shipped a fix OR have a written justification for deferring.
- [ ] All `minor`/`edge` bugs are filed in [#48](https://github.com/neboxdev/complidrop/issues/48) for the rolling backlog.
- [ ] [`README.md`](../../README.md) and marketing landing are up to date with any copy changes uncovered during QA.
- [ ] §16 known limitations are accepted (consciously, not by default) as launch-acceptable.
- [ ] Stripe is in **live** mode (was test during QA; flip before launch).
- [ ] Sentry, UptimeRobot, Resend domain auth (SPF/DKIM/DMARC) are live in production.
- [ ] Production env vars set, `ValidateOnStart()` boots cleanly, and the startup auto-migration applied cleanly (check boot logs).

### Post-QA cleanup

- [ ] Delete the test Stripe customers from the Stripe Dashboard test-mode list.
- [ ] Delete the test orgs from staging DB (or note them as "qa" orgs for future regression).
- [ ] Archive the fixtures folder OR keep it for the next regression pass.
- [ ] Update [`WORKLOG.md`](../../WORKLOG.md) with a short summary of what QA caught.

---

## Appendix — quick re-test list after a bug fix

When a bug is fixed mid-launch-prep, you don't need to re-run the whole plan. Run:

1. The exact failing step from the plan.
2. The 2–3 adjacent steps in the same section (regression sweep).
3. §1 smoke (5 min) to confirm nothing else broke.
4. §13 multi-tenancy (5 min) to confirm no isolation regression.

That's it. Full plan re-run only on major architectural changes.
