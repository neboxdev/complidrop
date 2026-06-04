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

## Table of contents

- [§0 — Setup & ground rules](#0--setup--ground-rules)
- [§1 — Smoke check (5 minutes)](#1--smoke-check-5-minutes)
- [§2 — First-time user journey (happy path, ~20 min)](#2--first-time-user-journey-happy-path-20-min)
- [§3 — Authentication flows](#3--authentication-flows)
- [§4 — Document management](#4--document-management)
- [§5 — Vendors](#5--vendors)
- [§6 — Vendor portal (external persona)](#6--vendor-portal-external-persona)
- [§7 — Compliance rules](#7--compliance-rules)
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
- [ ] **Database** — has migrations applied (`dotnet ef database update --context AppDbContext`). Seed data is fine but not required.
- [ ] **All secrets configured** — list per [CLAUDE.md](../../CLAUDE.md):
  - [ ] `Jwt:Secret` set
  - [ ] `AzureStorage:ConnectionString` set
  - [ ] `DocumentAi:*` configured (or `DocumentAi:Enabled=false` if testing without OCR)
  - [ ] `Extraction:Provider` set (`gemini` default)
  - [ ] `Gemini:ApiKey` (if `Endpoint=aistudio`) OR Vertex AI credentials
  - [ ] `Stripe:*` set to **test-mode** keys
  - [ ] `Resend:ApiKey` + `Resend:FromEmail` + `Resend:WebhookSecret` set
- [ ] **Stripe** — confirm test-mode in the Stripe dashboard ("View test data" toggle ON). If live-mode is on by accident, stop and switch.
- [ ] **Frontend `.env.local`** — `NEXT_PUBLIC_API_URL` points at the right backend.

### 0.2 Test data

- [ ] Fixture folder created per [`test-fixtures.md`](test-fixtures.md).
- [ ] Stripe test cards memorized: `4242…` (success), `4000 0000 0000 9995` (decline).
- [ ] Email aliases ready (`ruben+qaA@`, `ruben+qaB@`, `ruben+vendor@`).
- [ ] Resend dashboard tab open so you can verify outbound emails.

### 0.3 Browser setup

- [ ] Two browser profiles ready:
  - **Browser A** = "QA Admin A" (Chrome)
  - **Browser B** = "QA Vendor / Admin B" (Firefox or Edge — must differ from A so cookies don't bleed)
- [ ] DevTools open in both. **Network + Console panels visible.**
- [ ] Mobile device handy with the staging URL (or `ngrok` of localhost if on local dev).

### 0.4 Test discipline (read every time)

1. **One bug at a time.** If a test step fails, log it and *keep going*. Cascading failures often share a root cause and fixing one fixes many.
2. **Quote exact strings.** "Toast says 'Bad Gateway'" is actionable. "Got an error" is not.
3. **Capture correlation IDs.** DevTools → Network → failing request → Headers → `X-Trace-Id`. Paste into the bug report.
4. **Watch the Console.** A React warning or an uncaught promise that doesn't reach the UI is still a bug — it'll surface in Sentry on launch.
5. **Don't trust visual memory.** If you think the button changed color between two clicks, screenshot both. Memory lies.
6. **Each section assumes the previous account state.** §2 creates "Admin A". §3 reuses A. §13 creates "Admin B" in Browser B. Don't blow these away mid-plan.

### 0.5 Anti-expectations for the entire app (verify continuously)

These are project rules — if any appear, file a bug immediately, marked **launch-blocker**:

- [ ] **No raw HTTP jargon** in any toast or error card: "Bad Gateway", "Internal Server Error", "Failed to fetch", "TypeError", `(502)`, `(500)`, `NetworkError`. The generic fallback is **"Something went wrong. Try again."** — that's it.
- [ ] **No console errors** during normal navigation (warnings on hot-reload are fine, errors are not).
- [ ] **No exposed stack traces** in API responses (you'd see them in DevTools → Network → Response).
- [ ] **No flicker of an error before the data loads** — loading states must precede data, not the other way around.
- [ ] **No localhost or staging URLs** visible in production marketing copy (search the rendered HTML).

---

## §1 — Smoke check (5 minutes)

Before diving in, prove the app is alive.

- [ ] **1.1 Landing page loads.** Browser A → `http://localhost:3000/` (or staging URL). **Expect:** the marketing landing with headline "Stop Chasing Paper." / "Start Dropping Docs." renders within 3 seconds. **Don't expect:** a 404, a blank white page, or a Tailwind purge regression (unstyled HTML).
- [ ] **1.2 Sidebar nav (logged out).** Top right shows "**Log in**" + orange "**Get started**". **Don't expect:** "Go to dashboard" — that's the logged-in variant.
- [ ] **1.3 Backend healthcheck.** In a new tab: `http://localhost:5292/health/live`. **Expect:** `200 OK`, JSON body with `status: "Healthy"`. **Don't expect:** a 500 or a database-disconnected response.
- [ ] **1.4 API CORS.** From the DevTools console on the landing page: `await fetch('http://localhost:5292/api/auth/me', {credentials:'include'})`. **Expect:** a 401 response (not a CORS block). **Don't expect:** a red CORS error in console — that means the API isn't allowing `localhost:3000`.
- [ ] **1.5 Register and login URLs exist.** Click **"Log in"** → `/login` loads. Back. Click **"Get started"** → `/register` loads.
- [ ] **1.6 Pricing anchor.** On the landing, click **"See How It Works ↓"** — page should scroll to the `#how-it-works` section smoothly (no jump-cut). Then scroll down manually to the pricing section.

If any 1.* fails, stop and fix the environment. Don't proceed.

---

## §2 — First-time user journey (happy path, ~20 min)

The single most important test. This is what a real first customer experiences. Do it slowly, observing the *feel*, not just the function.

**Browser A. Fresh incognito or fresh profile.**

### 2.1 Landing → register

- [ ] **2.1.1** Land on `/`. **Expect:** hero with "Stop Chasing Paper." / "Start Dropping Docs.", two CTAs (orange "Get started free →" + outlined "See How It Works ↓"), and a sticky nav.
- [ ] **2.1.2** Hover the orange "Get started free →" button. **Expect:** it darkens to `#EA580C` and lifts slightly with a shadow. **Don't expect:** a flicker, a layout shift, or a missing cursor change.
- [ ] **2.1.3** Scroll through the page. **Expect:** four content sections in order — Problem, How It Works, Pricing, Who It's For — then the dark final CTA, then the footer. **Don't expect:** any image with broken `alt`, any "lorem ipsum" placeholder, any unstyled list.
- [ ] **2.1.4** Pricing section shows three tiles: **Free $0**, **Pro $49** (with orange "Most Popular" badge), **Annual $39/mo** (with sky-blue "Billed $468/year — save $120" subline).
- [ ] **2.1.5** Click the **Pro** tile's "Get started →" button. **Expect:** URL becomes `/register?plan=pro` and the form heading switches to **"Start your Pro account"** with a sky banner: **"You selected the Pro plan — $49/month. Cancel anytime."** Plus a "**Change**" link that goes back to `/#pricing`.
- [ ] **2.1.6** Go back. Click the **Annual** tile's "Get started →". **Expect:** `?plan=annual` and banner **"You selected the Annual plan — $39/month, billed $468/year. Save $120."**
- [ ] **2.1.7** Go back. Click the **Free** tile's "Start Free →". **Expect:** `?plan=free` and NO banner (Free has no `bannerCopy`).

### 2.2 Registration form validation

Still on `/register?plan=free`:

- [ ] **2.2.1** Submit empty form. **Expect:** four inline red errors appear under Full name, Company, Email, Password.
  - "Your full name is required"
  - "Company name is required"
  - "Enter a valid email"
  - "Password must be at least 12 characters"
  - **Don't expect:** a toast, a redirect, or only one error showing at a time.
- [ ] **2.2.2** Type `J` in Full name, `A` in Company. **Expect:** errors flip to "Your full name is required" / "Company name is required" still showing (min-2 chars).
- [ ] **2.2.3** Type a clearly bad email (`notanemail`). Submit. **Expect:** "Enter a valid email".
- [ ] **2.2.4** Fix the email to `ruben+qaA@gmail.com`. Type password `short`. Submit. **Expect:** "Password must be at least 12 characters".
- [ ] **2.2.5** Type `abcdefghijklm` (13 chars, all letters). Submit. **Expect:** "Password must include a digit".
- [ ] **2.2.6** Type `1234567890123` (13 chars, all digits). Submit. **Expect:** "Password must include a letter".
- [ ] **2.2.7** Type the help text below the password field. **Expect:** literal: "Min 12 chars, with a letter and a digit."

### 2.3 Successful registration

- [ ] **2.3.1** Fill the form:
  - Full name: `Ruben Garcia QA`
  - Company: `QA Admin A`
  - Email: `ruben+qaA@gmail.com` (or whatever email you control)
  - Password: `qa-launch-2026A`
  - Industry: (leave blank)
  - Size: (leave blank)
- [ ] **2.3.2** Click **"Create my account"**. **Expect:** button switches to **"Creating account…"** and disables.
- [ ] **2.3.3** Within ~1 second: redirect to `/dashboard`. **Expect:** success toast **"Account created. Welcome!"** appears bottom-right and auto-dismisses.
- [ ] **2.3.4** **Don't expect:** a flash of `/login` before `/dashboard`. **Don't expect:** an upgrade banner (you're on Free and the upgrade prompt lives only on `/settings`).

### 2.4 First impression of the dashboard

- [ ] **2.4.1** Greeting reads **"Welcome, Ruben"** (first name only). Subtitle: **"Here's a snapshot of your compliance posture."**
- [ ] **2.4.2** KPI strip: 4 cards. All counts should be **0**. Labels in order: **"Total documents"**, **"Compliant"**, **"Expiring ≤ 30d"**, **"Non-compliant"**.
- [ ] **2.4.3** Secondary row: 3 cards. **"Vendors tracked" = 0**, **"Awaiting extraction" = 0**, **"Compliance rate"** shows `—%` or `0%`.
- [ ] **2.4.4** Expiry pipeline: 5 vertical bars, all empty/zero. Labels visible under each: "Expired", "0-30d", "30-60d", "60-90d", "90d+".
- [ ] **2.4.5** Bottom row: a "Drop a document" card (link "Go to Documents →") and a "Recent activity" card. **Expect:** activity card shows **"No recent activity yet."** or **"Loading…"** briefly. **Don't expect:** a broken empty state ("undefined", "null", a JS error in console).
- [ ] **2.4.6** Left sidebar: navy background, white text, 7 nav items in order: Dashboard / Documents / Vendors / Vendor requirements / Reminders / Export / Settings. Dashboard is highlighted (active pill).
- [ ] **2.4.7** Sidebar footer: org name **"QA Admin A"** in bold, email below in grey, then a sky badge **"Free"** + a small grey "Log out" link.

### 2.5 First document upload

- [ ] **2.5.1** Click **Documents** in the sidebar. **Expect:** `/documents` loads. Heading "Documents". Subtitle "COIs, licenses, permits — dropped once, tracked forever." Right side small text **"0 total"**.
- [ ] **2.5.2** **Expect:** large dashed dropzone above the table with cloud-upload icon and copy: **"Drag a file here or click to browse"** + small line **"PDF, JPEG, PNG · 10 MB max"**.
- [ ] **2.5.3** Empty table state: **"No documents yet. Drop one above to get started."**
- [ ] **2.5.4** Drag `happy-path/sample-coi.pdf` over the dropzone. **Expect:** dashed border turns sky-blue, copy switches to **"Drop to upload…"**.
- [ ] **2.5.5** Drop. **Expect:** small sky text **"Uploading…"** appears below the dropzone description. Within ~2 seconds: a new table row appears with the filename as a sky-blue link, extraction badge = grey **"Pending"**.
- [ ] **2.5.6** Toast in the corner: **"Uploaded sample-coi.pdf"**.
- [ ] **2.5.7** **Don't expect:** a progress bar (there isn't one — known limitation §16). **Don't expect:** the row to be missing until you refresh — TanStack Query should re-fetch automatically.

### 2.6 Watch the extraction lifecycle

- [ ] **2.6.1** Within ~5 seconds the extraction badge should flip from grey **"Pending"** to a pulsing sky **"Processing"** (with subtle animation). Do not refresh — polling does this automatically.
- [ ] **2.6.2** Click the filename to open the detail page (`/documents/{id}`). **Expect:** "Loading document…" briefly, then the detail page.
- [ ] **2.6.3** Detail page header: filename as h1, document type uppercase below ("COI" or whatever was inferred). Right side: outline **"Re-extract"** button + sky **"View file"** link.
- [ ] **2.6.4** Four summary cells: EXTRACTION badge, COMPLIANCE badge (grey "Pending"), EXPIRES (`—` if not extracted yet), VERIFIED (`—`).
- [ ] **2.6.5** Extracted fields card shows **"Extraction in progress…"** while badge is Pending/Processing.
- [ ] **2.6.6** **Wait 30–90 seconds** for extraction to complete. Page polls every 3s; you do **not** need to refresh.
- [ ] **2.6.7** Badge flips to emerald **"Completed"** (with a confidence percentage on the list-page badge, e.g. **"Completed · 91%"** — but on the detail page, just **"Completed"**).
- [ ] **2.6.8** Extracted fields card populates: a 2-column grid of editable inputs. Each has a small uppercase field name, a value, and a colored confidence pill below ("**91% confident**" emerald if ≥90%, amber if ≥70%, rose if <70%).
- [ ] **2.6.9** **Don't expect:** a Save button to be active yet (Save changes is disabled until you edit something). **Don't expect:** any field labeled "✎ Manually edited" yet.
- [ ] **2.6.10** EXPIRES cell now shows a localized date (or `—` if the doc didn't have one).
- [ ] **2.6.11** Open DevTools → Network. **Expect:** polling stopped — no `/api/documents/{id}` request being made every 3s anymore. **Don't expect:** continuous background traffic after extraction is done.

### 2.7 Edit and save a field

- [ ] **2.7.1** Find a field with low confidence (rose pill, < 70%). If none exist, pick any field.
- [ ] **2.7.2** Edit the value (type something new). **Expect:** the **"Save changes"** button at the top of the fields card becomes enabled.
- [ ] **2.7.3** Click **"Save changes"**. **Expect:** toast **"Fields updated"**. Edits clear, page re-renders with new values. The edited field now has a sky tag **"✎ Manually edited"** and a small grey line **"was: {original value}"**. Confidence pill on that field flips to emerald **"100% confident"**.
- [ ] **2.7.4** VERIFIED summary cell now shows emerald **"✓ Yes"** with a shield icon.
- [ ] **2.7.5** **Don't expect:** a confirmation modal before saving. **Don't expect:** other fields to be marked as edited.

### 2.8 Re-extract

- [ ] **2.8.1** Click **"Re-extract"** in the header. **Expect:** small rotating refresh icon on the button, toast **"Re-extraction queued"**.
- [ ] **2.8.2** Extraction badge flips back to **"Pending"** then **"Processing"**, fields card shows "Extraction in progress…" again.
- [ ] **2.8.3** Wait for it to complete a second time. **Expect:** badge returns to **"Completed"**, fields populated. The **manually edited values you saved in 2.7 may or may not be overwritten** depending on the implementation — note the actual behavior in your bug log if it feels wrong (e.g. if a manually verified field is silently replaced, that's likely a UX gap, not a bug per se).

### 2.9 Sign out and sign back in

- [ ] **2.9.1** Click "Log out" in the sidebar footer. **Expect:** redirect to `/login` (no confirmation dialog). **Don't expect:** any toast or "you've been signed out" message.
- [ ] **2.9.2** **Expect:** `/login` shows "Welcome back" with email + password fields and "Sign in" button.
- [ ] **2.9.3** Type the same email + password from 2.3.1. Submit. **Expect:** button → **"Signing in…"**, then redirect to `/dashboard`, toast **"Welcome back!"**.
- [ ] **2.9.4** Dashboard now shows **"Total documents: 1"**, **"Awaiting extraction: 0"** (extraction is done), and the activity card should show entries like **"Document · Uploaded"** and **"User · Logged in"** with localized timestamps.

### 2.10 First-time UX impressions to log as findings

Not pass/fail — write down anything that *felt* off:

- [ ] Did you ever feel "what do I do next?" — log as UX finding (deferrable).
- [ ] Was any copy ambiguous or jargony for a SMB user?
- [ ] Did the polling cadence feel jumpy or jittery?
- [ ] Were colors consistent — did the same status badge ever look different in two places?

---

## §3 — Authentication flows

### 3.1 Login form validation

- [ ] **3.1.1** Log out. Land on `/login`. Submit empty. **Expect:** inline errors **"Enter a valid email"** + **"Password is required"**.
- [ ] **3.1.2** Type a valid email, empty password. **Expect:** only password error remains.
- [ ] **3.1.3** Type a malformed email (`not@an@email`). **Expect:** "Enter a valid email".

### 3.2 Bad credentials & lockout

- [ ] **3.2.1** Type your QA Admin A email + wrong password (`qa-launch-2026X`). Submit. **Expect:** toast **"Invalid email or password."** Button returns to **"Sign in"**.
- [ ] **3.2.2** Repeat 8 more times (total 9 fails). **Expect:** still **"Invalid email or password."** No lockout yet.
- [ ] **3.2.3** Repeat one more time (10th fail). **Expect:** still **"Invalid email or password."** Then try once more (11th). **Expect:** toast **"Account temporarily locked. Try again later."**
- [ ] **3.2.4** Type the **correct** password now. Submit. **Expect:** still locked (**"Account temporarily locked. Try again later."**).
- [ ] **3.2.5** **Wait 15 minutes** (skip this step if time-constrained; just note the lockout works). Try again — should succeed. (You can also manually unlock by clearing `LockedUntil` in the DB if testing offline.)
- [ ] **3.2.6** **Don't expect:** the error message to reveal whether the email exists or not. Both bad email AND wrong password should produce the same "Invalid email or password." text.

### 3.3 Rate limiting

The `auth-strict` policy is **5 requests / minute / IP** on login + register + refresh.

- [ ] **3.3.1** After being unlocked (or in a fresh browser), submit the login form rapidly 6 times in under a minute (any password). **Expect:** the 6th attempt's toast is **"Too many requests. Please try again later."** (code `rate_limit.exceeded`).
- [ ] **3.3.2** Look at DevTools → Network on the 6th request. Response status: 429. Response headers include `Retry-After`.
- [ ] **3.3.3** Wait one minute. Try again. **Expect:** can submit.

### 3.4 Registration edge cases

- [ ] **3.4.1** Try to register the same email (`ruben+qaA@gmail.com`) again. **Expect:** toast **"An account with that email already exists."** **Don't expect:** a 500, or a duplicate org silently created.
- [ ] **3.4.2** Register with a valid new email + password but leave name and company at one character. **Expect:** zod errors block submission before the network call.
- [ ] **3.4.3** Submit register with `?plan=funky` in the URL. **Expect:** form still renders with the default "Start dropping docs" heading (unknown plan falls back to Free).

### 3.5 Session persistence

- [ ] **3.5.1** Log in as Admin A. Open a new tab to `/documents`. **Expect:** session persists, you land on documents (not redirected to /login). Cookies are httpOnly so you can't see them in JS, but in DevTools → Application → Cookies you should see `cd_session`, `cd_refresh`, `cd_session_hint`.
- [ ] **3.5.2** Close the browser tab. Reopen. Visit `/dashboard`. **Expect:** still logged in.
- [ ] **3.5.3** **Don't expect:** the session cookie to leak into JS (`document.cookie` should NOT show `cd_session` — that's the httpOnly guarantee).

### 3.6 Silent session refresh

The session cookie expires after 15 minutes; the frontend silently refreshes via `cd_refresh` on a 401.

- [ ] **3.6.1** (Optional, time-intensive) Stay logged in for 20+ minutes without activity. Then click any nav link. **Expect:** the page loads normally — the user sees nothing visible despite the session having expired. DevTools → Network shows a `POST /api/auth/refresh` request before the user-initiated request.
- [ ] **3.6.2** (Faster alternative) In DevTools → Application → Cookies, manually delete `cd_session` (keep `cd_refresh`). Click a nav link. **Expect:** silent refresh, page loads. **Don't expect:** a flash of /login or a toast.
- [ ] **3.6.3** Delete BOTH `cd_session` AND `cd_refresh`. Click a nav link. **Expect:** toast **"Session expired. Please log in again."** on whatever action you tried. **Note:** the user is NOT auto-redirected to /login (see §16 — this is a known limitation, not a bug).

### 3.7 Logout behavior

- [ ] **3.7.1** Click "Log out". **Expect:** redirect to `/login` immediately. All three cookies cleared (verify in DevTools → Application).
- [ ] **3.7.2** Press the browser back button. **Expect:** the previous dashboard page momentarily loads from cache, then redirects to `/login` (auth gate kicks in via `useMe()`).
- [ ] **3.7.3** **Don't expect:** any logged-in data to still be visible after logout.

### 3.8 No forgot-password flow

- [ ] **3.8.1** On `/login` look carefully. **Expect:** there is NO "Forgot password?" link. This is intentional — see §16. **Don't expect:** a broken/disabled link or a 404 on click. If you find one, that's a real bug — log it.

---

## §4 — Document management

Reuse the QA Admin A account. Should already have 1 document from §2.

### 4.1 Empty/non-empty states

- [ ] **4.1.1** Navigate to `/documents`. **Expect:** "1 total" in the header, the table shows your sample-coi.pdf row.
- [ ] **4.1.2** Re-read the column headers in the table. Order should be: **File / Type / Vendor / Extraction / Compliance / Expires / (no header for actions)**.

### 4.2 Single-file upload (PDF)

- [ ] **4.2.1** Click anywhere in the dropzone (not drag). **Expect:** native file picker opens.
- [ ] **4.2.2** Select `happy-path/sample-license.pdf`. **Expect:** dropzone briefly shows "Uploading…", row appears, toast **"Uploaded sample-license.pdf"**.
- [ ] **4.2.3** Watch the row. **Expect:** badge transitions Pending → Processing → Completed (or ManualRequired, depending on the doc).
- [ ] **4.2.4** **Don't expect:** the page to lose its scroll position when the row updates.

### 4.3 Multi-file upload

- [ ] **4.3.1** Select all three `happy-path/*.pdf` files at once in the file picker (Ctrl+A in the dialog). **Expect:** three toasts in quick succession: **"Uploaded sample-coi.pdf"**, **"Uploaded sample-license.pdf"**, **"Uploaded sample-permit.pdf"**. Three rows added.
- [ ] **4.3.2** **Don't expect:** all uploads to complete before the next one starts (they're sequential by design, but each shows its own toast).

### 4.4 File-type rejection

- [ ] **4.4.1** Drag `validation-edge/docx-disallowed.docx` onto the dropzone. **Expect:** silently rejected on the dashboard (the dashboard dropzone doesn't show explicit rejection toasts — known UX gap; portal page does show them). The file should NOT appear in the list. **Note:** if a toast does appear with the correct rejection copy, even better — log as a positive surprise.
- [ ] **4.4.2** Drag `validation-edge/fake.pdf` (text file renamed). **Expect:** dropzone accepts it (filename ends in .pdf so client-side passes), upload proceeds, server rejects with **"Only PDF, JPEG, and PNG files are supported."** toast. **Don't expect:** the server to accept fake.pdf — magic-byte validation must catch it.
- [ ] **4.4.3** Drag `validation-edge/fake.jpg` (PDF renamed). Same expectation: server rejects via magic-byte check.

### 4.5 File-size rejection

- [ ] **4.5.1** Upload `validation-edge/exactly-10mb.pdf`. **Expect:** accepted (10485760 bytes = exactly at the limit). Note: the PDF will likely fail extraction (it's mostly empty padding), so badge will end at Failed or ManualRequired — that's fine, the test is about size acceptance, not extraction quality.
- [ ] **4.5.2** Upload `validation-edge/over-10mb.pdf` (10485761+ bytes). **Expect:** toast **"File exceeds the 10 MB limit."** Row does NOT appear. **Don't expect:** the upload to hang or show "Bad Gateway".
- [ ] **4.5.3** Upload `validation-edge/empty.pdf` (0 bytes). **Expect:** toast **"Upload a PDF, JPEG, or PNG file."** or similar empty-file error.

### 4.6 Image uploads

- [ ] **4.6.1** Upload `validation-edge/jpeg-real.jpg`. **Expect:** accepted. Extraction may produce few/no fields if it's not a real compliance doc — that's OK.
- [ ] **4.6.2** Upload `validation-edge/png-real.png`. Same expectation.
- [ ] **4.6.3** Upload `extraction-edge/photo-of-coi.jpg`. **Expect:** accepted. Extraction may complete with low confidence (ManualRequired) — that's the OCR robustness test.

### 4.7 Detail page

Pick a fully-extracted document.

- [ ] **4.7.1** Click the filename. **Expect:** detail page loads (`/documents/{guid}`) within ~1s.
- [ ] **4.7.2** Click **"← All documents"**. **Expect:** back to `/documents`.
- [ ] **4.7.3** Click "View file" on the detail page. **Expect:** the original blob opens in a new tab. PDF renders, JPEG/PNG renders.
- [ ] **4.7.4** **Don't expect:** the file URL to be downloadable without authentication. (The blob URL may use SAS tokens — verify by copying the URL and pasting it in an incognito window. It should still work if SAS-signed; if it requires the cookie, that's also fine. The test is "the user can view their file".)

### 4.8 Document not found

- [ ] **4.8.1** Manually navigate to `/documents/00000000-0000-0000-0000-000000000000`. **Expect:** page shows **"Document not found."** with a sky link **"Back to documents"**.
- [ ] **4.8.2** **Don't expect:** a 500 error, a stack trace, or the full error card with retry button (that's for 5xx, not 404).

### 4.9 Delete document

- [ ] **4.9.1** Hover the trash-can icon on any row. **Expect:** ghost button highlight.
- [ ] **4.9.2** Click it. **Expect:** browser-native `confirm()` dialog: **"Remove {filename}?"**
- [ ] **4.9.3** Click Cancel. **Expect:** no change.
- [ ] **4.9.4** Click again, then OK. **Expect:** toast **"Document removed"**, row disappears from the table.
- [ ] **4.9.5** **Don't expect:** a permanent deletion (DB has soft-delete via `DeletedAt`). If you re-upload the same filename, you should get a new row, not a "duplicate" warning.

### 4.10 Polling (live updates without refresh)

- [ ] **4.10.1** Open `/documents` in two tabs (both as Admin A). Upload a new PDF in tab 1.
- [ ] **4.10.2** Switch to tab 2. **Expect:** within ~5 seconds, the new row appears without manual refresh (TanStack Query polling).
- [ ] **4.10.3** Watch the badge in tab 2. **Expect:** it transitions Pending → Processing → Completed/ManualRequired with no refresh.
- [ ] **4.10.4** **Don't expect:** the polling to continue forever after extraction completes. After ~10s of being in a terminal state, network traffic should be quiet.

### 4.11 Stale-data banner (intentional 5xx)

- [ ] **4.11.1** Stop the API server (`Ctrl+C` the `dotnet watch run` terminal). Wait 5s.
- [ ] **4.11.2** Click somewhere on the documents page that triggers a refetch (or wait for polling to fire). **Expect:** amber StaleDataBanner appears above the table with **"Couldn't refresh documents"** + small subline showing the error message (or generic fallback), and a **"Try again"** button. The table itself still shows cached data. **Don't expect:** raw "Bad Gateway" or "TypeError" in the subline.
- [ ] **4.11.3** Restart the API. Click **"Try again"** in the banner. **Expect:** rotating icon while retrying, then banner disappears, data refreshes.

### 4.12 Error state (no cached data)

- [ ] **4.12.1** Log out, stop the API, log back in. As you visit `/documents`, the request fails with no cached data. **Expect:** full alert card (rose triangle icon) with **"Couldn't load documents."** + server message + **"Retry"** button. `role="alert"`.
- [ ] **4.12.2** Restart API, click Retry. **Expect:** data loads.

### 4.13 Status-badge color contract

Verify these by uploading multiple files and observing:

- [ ] **4.13.1 Pending** — slate (grey) pill. No animation.
- [ ] **4.13.2 Processing** — sky pill with `animate-pulse` (subtle breathing animation).
- [ ] **4.13.3 Completed** — emerald (green) pill with confidence suffix on list, just "Completed" on detail page.
- [ ] **4.13.4 ManualRequired** — amber (yellow) pill. (Hardest to trigger reliably — `extraction-edge/low-confidence.pdf` is your best bet.)
- [ ] **4.13.5 Failed** — rose (red) pill. (Trigger by uploading `validation-edge/tiny.pdf` if the LLM can't extract anything, OR by stopping the API mid-extraction and watching it eventually time out — slow test.)

### 4.14 Compliance-status badge (preview, full test in §7)

- [ ] **4.14.1** On a freshly uploaded document with no vendor and no template assigned, compliance badge should be slate **"Pending"** indefinitely (no rules to evaluate against).

---

## §5 — Vendors

### 5.1 Empty vendors page

- [ ] **5.1.1** Navigate to `/vendors`. **Expect:** heading "Vendors", subtitle "Manage subcontractors and their compliance documents.", an "Add vendor" card at top, and table empty state **"No vendors yet."**.

### 5.2 Add vendor form

- [ ] **5.2.1** Try to click **"+ Add vendor"** with both fields empty. **Expect:** button is disabled.
- [ ] **5.2.2** Type "Mike's Electrical" in Name (no email). **Expect:** button enables (email is optional).
- [ ] **5.2.3** Click **"+ Add vendor"**. **Expect:** toast **"Vendor added"**, inputs clear, row appears in the table.
- [ ] **5.2.4** Add another: Name "ACME Plumbing", Email "ruben+vendor@gmail.com". **Expect:** row shows the email below the name.

### 5.3 Vendor detail / edit

- [ ] **5.3.1** Click "Manage ↗" on Mike's Electrical row. **Expect:** detail page at `/vendors/{guid}`.
- [ ] **5.3.2** **Expect:** form with Name, Contact email, Contact phone, Category, Compliance template (dropdown).
- [ ] **5.3.3** "What this vendor must prove" dropdown (#190): first option **"— No requirements set —"** + the seeded checklists ("Caterer", "Event Rental Company", "Security Service", "Transportation / Shuttle", "Photographer / Videographer"). With "— No requirements set —" selected, an amber note warns the vendor's documents won't be marked covered. **Don't expect:** an empty dropdown — if NO checklists are seeded, log a finding.
- [ ] **5.3.4** Update phone, category. Click **"Save changes"**. **Expect:** toast **"Vendor updated"**.
- [ ] **5.3.5** Change the compliance template to one of the system templates. Save. **Expect:** toast, page re-renders with template selected.

### 5.4 Portal upload links

- [ ] **5.4.1** Scroll to "Portal upload links" card. **Expect:** empty state **"No links yet — generate one above."**
- [ ] **5.4.2** Click **"Generate upload link"**. **Expect:** within 1s the link is created, the URL is auto-copied to clipboard, toast **"Link copied to clipboard"**.
- [ ] **5.4.3** **Expect:** a list item appears with the full URL in a read-only monospace input, a copy icon button, an emerald **"active"** badge, and **"0/20 uploads"**.
- [ ] **5.4.4** Paste the URL somewhere (text editor) to verify it's the actual link. It should be `http://localhost:3000/portal/{some-32-char-token}` (or staging URL).
- [ ] **5.4.5** Click the small copy-icon button. **Expect:** toast **"Copied"**.
- [ ] **5.4.6** Generate a second link. **Expect:** two list items, both showing "active".
- [ ] **5.4.7** Click the ✕ icon on the second link. **Expect:** link disappears IMMEDIATELY without confirmation (no toast). **Note:** the rules and CLAUDE.md mention that vendor portal-link revokes don't show a confirmation — verify this is the intended UX (you may want to add one as a polish ticket post-launch).

### 5.5 Vendor with documents

- [ ] **5.5.1** Go back to `/documents`. Look at the existing documents — none have a vendor yet (the Vendor column shows `—`).
- [ ] **5.5.2** Hmm — there's no "assign vendor to document" UI surfaced on the documents detail page. Verify this. If you can assign via the API (POST upload with vendorId), that's not user-testable here. **Action:** log this as a UX finding — "Cannot assign vendor to existing document from the UI" — and check if the design intent is that vendors are only assigned at upload time (via portal upload).
- [ ] **5.5.3** Back on `/vendors`, the Docs column should show 0 for both vendors (no docs assigned yet). After §6 portal uploads, this should change.

### 5.6 Vendor delete

- [ ] **5.6.1** **Note:** there's no delete-vendor button visible in the QA exploration — the API endpoint exists (`DELETE /api/vendors/{id}`) but UI may or may not surface it. Verify and log if it's missing.

---

## §6 — Vendor portal (external persona)

This is the critical viral-loop UX. Test on actual mobile.

**Browser B** (different profile than Browser A) or **mobile**. NOT logged in.

### 6.1 Open the portal link

- [ ] **6.1.1** Copy a portal link from §5.4. Paste into Browser B. **Expect:** within 1 second the page loads. Centered card, sky background.
- [ ] **6.1.2** Top: small sky chip **"Secure upload"** with shield icon.
- [ ] **6.1.3** Heading: **"Hi Mike's Electrical"** (the vendor's name).
- [ ] **6.1.4** Subtitle: **"QA Admin A asked for your latest compliance documents. Drop them here."**
- [ ] **6.1.5** Dropzone with cloud icon, copy "Drag a file here or click to select" + helper "PDF, JPEG, or PNG · 10 MB max".
- [ ] **6.1.6** Counter line: **"0 / 20 uploads used on this link"**.
- [ ] **6.1.7** Footer: small grey **"Powered by CompliDrop"** (with "CompliDrop" in sky).
- [ ] **6.1.8** **Don't expect:** any nav, sidebar, login prompt, signup prompt, or "your CompliDrop dashboard" link. This is a single-purpose page.
- [ ] **6.1.9** **Don't expect:** any reference to QA Admin A's email or org details beyond the name (no leakage).

### 6.2 Vendor uploads happy path

- [ ] **6.2.1** Drop `happy-path/sample-coi.pdf` on the dropzone. **Expect:** "Uploading…" appears, then the dropzone replaces with a green "Received" card listing **"sample-coi.pdf — Processing…"**.
- [ ] **6.2.2** Counter updates to **"1 / 20 uploads used on this link"**.
- [ ] **6.2.3** Upload a second file. **Expect:** the Received card lists both files. Counter **"2 / 20"**.

### 6.3 Vendor file rejections

- [ ] **6.3.1** Try to drop `validation-edge/docx-disallowed.docx`. **Expect:** an `aria-live` rose error appears: **"That file type isn't accepted. Please upload a PDF, JPEG, or PNG."**
- [ ] **6.3.2** Try to drop `validation-edge/over-10mb.pdf`. **Expect:** **"That file is too large. The 10 MB cap is per file — try splitting it or compressing it."**
- [ ] **6.3.3** Try to drop `validation-edge/empty.pdf`. **Expect:** **"That file is empty."**
- [ ] **6.3.4** Try to drop two files at once. **Expect:** **"Please drop one file at a time."**
- [ ] **6.3.5** Try `validation-edge/fake.pdf`. **Expect:** server rejects with the unsupported-format error displayed in the same rose region.
- [ ] **6.3.6** **Don't expect:** any error message exposing internal codes ("document.unsupported_format") to the vendor. The user-facing copy must be friendly.

### 6.4 Customer side: verify uploads arrived

Switch to Browser A.

- [ ] **6.4.1** As Admin A, refresh `/documents`. **Expect:** 2 new rows appear with the same filenames the vendor uploaded. Vendor column shows **"Mike's Electrical"**.
- [ ] **6.4.2** Open one. **Expect:** detail page works, fields extract. The metadata `UploadedBy` should be "vendor_portal" (not visible in UI, just verify the behavior is correct).
- [ ] **6.4.3** Navigate to `/vendors`. **Expect:** Mike's Electrical row now shows **"Docs: 2"**.

### 6.5 Rate limit (per token)

The token allows 10 uploads/hour. Slow test, but worth doing once.

- [ ] **6.5.1** From Browser B, upload 10 files rapidly. **Expect:** all 10 succeed.
- [ ] **6.5.2** Upload the 11th. **Expect:** rose error **"Too many requests. Please try again later."** PLUS a small slate subline **"Try again in about an hour, or retry now."** AND a **"Retry upload"** button that re-submits the same file.
- [ ] **6.5.3** Click "Retry upload" immediately. **Expect:** still rate-limited (same error).
- [ ] **6.5.4** **Wait the configured window** (or have someone reset the limit in the DB). Try again — should succeed.

### 6.6 Quota exhaustion (per link)

The link defaults to 20 uploads max. Combined with §6.5, you may have already used 10. Upload 10 more (total 20).

- [ ] **6.6.1** Upload until counter reaches **"20 / 20"**. The 20th upload should still succeed.
- [ ] **6.6.2** Drop a 21st file. **Expect:** rose error **"Upload quota reached for this link."** subline **"This link is exhausted. Please ask your customer to send you a new upload link."** **NO retry button** (intentional).
- [ ] **6.6.3** Even after refreshing the page, the dropzone shows **"Upload limit reached on this link"** with reduced opacity.
- [ ] **6.6.4** Customer side (Browser A): navigate to the vendor's detail page. **Expect:** the link's badge has changed from emerald **"active"** to slate **"inactive"**. Counter shows **"20 / 20"** still.

### 6.7 Revoked / inactive link

- [ ] **6.7.1** Customer side: generate a fresh portal link for the same vendor. Revoke it immediately (the ✕ button in §5.4.7).
- [ ] **6.7.2** Open the revoked link's URL in Browser B. **Expect:** full-page replacement with **"This link is no longer available."** + **"Ask your customer for a fresh upload link."**.
- [ ] **6.7.3** **Don't expect:** the vendor name or the customer org name to leak when the link is invalid.

### 6.8 Bogus token

- [ ] **6.8.1** In Browser B, manually navigate to `/portal/this-is-not-a-real-token`. **Expect:** same "This link is no longer available." page.

### 6.9 Mobile portal page

Use a real phone, not just DevTools emulation.

- [ ] **6.9.1** Open a fresh portal link on the phone. **Expect:** page loads in under 2 seconds even on 4G.
- [ ] **6.9.2** The dropzone is large enough to tap easily. Counter is legible.
- [ ] **6.9.3** Tap the dropzone. **Expect:** native iOS/Android file picker opens. Pick a photo from camera roll OR take a new photo with the camera. **Expect:** upload proceeds with "Uploading…", then "Received".
- [ ] **6.9.4** **Don't expect:** a horizontal scroll, broken layout, or the keyboard pushing content off-screen.
- [ ] **6.9.5** **Don't expect:** an "Open in app" prompt or any reference to a CompliDrop mobile app (there isn't one).

### 6.10 Portal page should NOT poll

- [ ] **6.10.1** After uploading a file, watch DevTools → Network on the portal page. **Expect:** no continuous traffic. The vendor sees "Processing…" but the portal doesn't poll for extraction status. **Don't expect:** repeated GET requests every 3 seconds.

---

## §7 — Compliance rules

> **Rebuilt in #192.** The page is now plain-English **"Vendor requirements"** — no field-name/operator typing, no "Templates" / "system" jargon. Requirements are picked from a menu and shown as sentences.

### 7.1 Vendor requirements list

- [ ] **7.1.1** Navigate to `/rules`. **Expect:** header "Vendor requirements" + subhead "Set what each kind of vendor must prove…". Two-column layout.
- [ ] **7.1.2** Left rail: **"Your checklists"** (empty-state copy if none) and **"Suggested checklists"** with the venue-type names "Caterer", "Event Rental Company", "Security Service", "Transportation / Shuttle", "Photographer / Videographer" — each with a **"Use this"** button (NO slate "system" badge). **Don't expect:** the old generic trade names or a "Templates" heading.
- [ ] **7.1.3** Above the list: a labeled **"New checklist"** input + a `+` button (disabled until a name is typed).

### 7.2 Create a checklist

- [ ] **7.2.1** Type "QA Caterer" + click `+`. **Expect:** toast **"Checklist created"**, new entry under "Your checklists", auto-selected.
- [ ] **7.2.2** Main panel shows the checklist name as h2 + an outline **"Delete checklist"** button + a **"+ Add a requirement"** button. **Don't expect:** any doc-type / field / operator / message columns.

### 7.3 Add requirements in plain English

- [ ] **7.3.1** Click **"+ Add a requirement"** → group **Insurance** → **"General liability — minimum coverage"**. Pick the **$1,000,000** preset (or type into the numeric field). Click **Add**. **Expect:** a sentence row "Carries at least $1,000,000 in general liability insurance" with a green check. **Don't expect:** to type "general_liability_limit", "min_value", or an un-formatted number.
- [ ] **7.3.2** Click **"+ Add a requirement"** → group **Dates** → **"Document must not be expired"** → Add. **Expect:** "Insurance has not expired" + honest helper text (no "future date" promise). A live **"A QA Caterer is compliant when…"** summary appears and includes both requirements.
- [ ] **7.3.3** **Don't expect:** the Add button to be enabled before a money/text value is entered — it shows a reason ("Enter a coverage amount").

### 7.4 Edit / remove a requirement

- [ ] **7.4.1** Click the pencil (aria-label "Edit requirement: …") on the general-liability row. **Expect:** the money field pre-fills as **"$1,000,000"**. Change to $2,000,000, click **Save**. **Expect:** the sentence updates (no duplicate row).
- [ ] **7.4.2** Click the trash icon (aria-label "Remove requirement: …"). **Expect:** the requirement disappears immediately. **No confirmation, no toast.**

### 7.5 Suggested checklists clone into an editable copy

- [ ] **7.5.1** Click **"Use this"** on a suggested checklist (e.g. "Caterer"). **Expect:** toast **"Checklist added — edit it to fit your vendors"**, a new editable copy appears under "Your checklists" and is selected (with Edit/Remove affordances). The original suggestion stays under "Suggested checklists".

### 7.6 Apply a checklist to a vendor

- [ ] **7.6.1** Navigate to `/vendors/{Mike's Electrical id}`. Set **"What this vendor must prove"** to "QA Caterer". Save.
- [ ] **7.6.2** Navigate to a document the vendor uploaded (one of the COIs from §6). **Expect:** compliance check should run.
- [ ] **7.6.3** Trigger a fresh compliance evaluation: re-extract the document, OR if there's a manual "Run check" button, use it. **Expect:** within ~10s the compliance badge changes:
  - If extracted `general_liability_limit` ≥ 1000000 AND `expiration_date` is present → emerald **"Compliant"**.
  - If `expiration_date` is in the past → rose **"Expired"** (overrides everything).
  - If `expiration_date` is in the next 30 days → amber **"ExpiringSoon"** (only when other rules pass).
  - If `general_liability_limit` < 1000000 or missing → rose **"NonCompliant"**.

### 7.7 Delete custom template

- [ ] **7.7.1** Back to `/rules`, select "QA Custom Template". Click **"Delete template"**. **Expect:** browser confirm dialog **"Delete QA Custom Template?"**.
- [ ] **7.7.2** Cancel. **Expect:** no change.
- [ ] **7.7.3** Try again, OK. **Expect:** toast **"Template removed"**, template disappears from sidebar, main panel resets to "Select or create a template to edit its rules."

---

## §8 — Reminders

### 8.1 Default reminders seeded

- [ ] **8.1.1** Navigate to `/reminders`. **Expect:** heading "Reminders", subtitle "Sent automatically at 8 AM in your org's local time zone."
- [ ] **8.1.2** **Expect:** 4 rows in the configuration table (60, 30, 14, 7 days before). Each shows "Notify team" and "Notify vendor" toggles and an "Active" toggle. Slider colors: sky-blue when on, slate when off.
- [ ] **8.1.3** Per the spec: 60-day reminder defaults: team ON, vendor OFF. 30-day: team ON, vendor ON. 14-day: team ON, vendor ON. 7-day: team ON, vendor ON. All Active by default. Verify these defaults match.

### 8.2 Toggle behavior

- [ ] **8.2.1** Toggle off the 60-day reminder. **Expect:** slider switches. NO toast. The change persists on page refresh (verify by refreshing).
- [ ] **8.2.2** **Note:** the absence of a confirmation toast on toggle is intentional per the spec but may feel "did it work?" — log as a UX finding if it bothers you.

### 8.3 Recent deliveries

- [ ] **8.3.1** Below the config table, **"Recent deliveries"** card. If you've not had any documents expire yet, **expect:** empty state **"No reminders sent yet."**
- [ ] **8.3.2** **Force a reminder send** (for testing). Options:
  - **Easiest:** edit one of your test documents in the DB directly to set its `ExpirationDate` to today + 7 days (matching one of the active reminders). Then wait for the next 08:00 in your org's local TZ.
  - **Time-sensitive:** if you're testing on local dev with the API server running, restart the server with `Organization.TimeZone` overridden, OR shift your computer's clock to mimic 08:00 (risky, breaks Stripe).
  - **Practical for QA:** verify the rendering works by manually inserting a fake `ReminderLog` row in the DB and checking the page.
- [ ] **8.3.3** Once a reminder has fired, **expect:** table with 3 columns **When / Recipient / Status**. Status badge:
  - **delivered** — emerald
  - **bounced / complained / failed** — rose
  - Other (sent, queued) — sky

### 8.4 Email content (when you can capture one)

When a reminder actually sends:

- [ ] **8.4.1** Subject line: **"[QA Admin A] sample-coi.pdf expires in 7 days"** (or whatever the matching reminder + doc is).
- [ ] **8.4.2** Body opens with sky-blue heading **"Compliance reminder"**.
- [ ] **8.4.3** Body text: "Hi there, Your document **{filename}** from **{vendorName or 'a vendor'}** expires on **{Month D, YYYY}** — that's {N} days from today."
- [ ] **8.4.4** CTA line: "Log in to QA Admin A on CompliDrop to review and upload the renewal."
- [ ] **8.4.5** Footer: small grey **"Sent automatically by CompliDrop. You can adjust reminder cadence in Settings → Reminders."**
- [ ] **8.4.6** **Don't expect:** any tracking pixel or "Click here to unsubscribe" (this is transactional). **Don't expect:** raw HTML showing in the inbox client.

### 8.5 Resend dashboard verification

- [ ] **8.5.1** Open Resend dashboard. Verify the email shows up with status **delivered** (or **sent → delivered** within a minute).
- [ ] **8.5.2** Wait 30+ seconds, refresh `/reminders` in the app. **Expect:** the recent deliveries table shows the email with status **delivered** (Resend webhook updated it).

---

## §9 — Export

### 9.1 PDF audit report

- [ ] **9.1.1** Navigate to `/export`. **Expect:** heading "Export", subtitle "Download audit-ready reports and raw data.", two cards (PDF + CSV).
- [ ] **9.1.2** PDF card: header "PDF audit report" with file icon, description copy, two date inputs (From, To) with calendar icons. Defaults: From = 30 days ago, To = today.
- [ ] **9.1.3** Click **"Download PDF"**. **Expect:** button disables briefly, toast **"Download started"**, browser shows a PDF download (or auto-opens, depending on browser settings).
- [ ] **9.1.4** Open the downloaded file. **Expect:** filename like `complidrop-audit-2026-04-28-2026-05-28.pdf`. Inside:
  - Header: large sky **"CompliDrop Audit Report"** + your org name + "Generated {date}".
  - Documents table: every active doc with File / Vendor / Type / Expires / Compliance.
  - Audit log table: last 500 events in range, columns When / Action / Entity / User.
  - Footer: "CompliDrop · QA Admin A".
- [ ] **9.1.5** **Don't expect:** any other org's documents, any deleted documents, or any PII you didn't enter.

### 9.2 Date range edge cases

- [ ] **9.2.1** Set From = today + 30 days (future). Download. **Expect:** PDF generates with empty Documents table (or just headers) — no error. **Don't expect:** a 500.
- [ ] **9.2.2** Set From = 2 years ago. Download. **Expect:** still works, includes everything in range.
- [ ] **9.2.3** Set From > To (invalid range). **Expect:** either a validation message OR the PDF gracefully handles it. Log whichever you see.

### 9.3 CSV export

- [ ] **9.3.1** CSV card: header "CSV export" with spreadsheet icon.
- [ ] **9.3.2** Click **"Download CSV"**. **Expect:** toast "Download started", file downloads. Filename `complidrop-documents-{today}.csv`.
- [ ] **9.3.3** Open in Excel/Sheets. **Expect:** columns Id, FileName, Vendor, Type, Status, Compliance, EffectiveDate, ExpirationDate, GeneralLiabilityLimit, UploadedBy, CreatedAt.
- [ ] **9.3.4** **Don't expect:** any soft-deleted documents in the CSV. **Don't expect:** another org's data.

### 9.4 Single-vendor PDF (KNOWN GAP)

- [ ] **9.4.1** The endpoint `GET /api/export/vendor/{id}` exists but is NOT surfaced in the UI. This is a known gap (§16). **Don't try to test it as a user.** Log a future ticket if you want this exposed pre-launch.

### 9.5 Error handling

- [ ] **9.5.1** Stop the API. Click "Download PDF". **Expect:** toast **"Something went wrong. Try again."** (the GENERIC_FALLBACK_MESSAGE). **Don't expect:** "Bad Gateway", "Failed to fetch", or `(502)`.
- [ ] **9.5.2** Restart API. Retry — should work.

---

## §10 — Billing

**Stripe test mode confirmed?** If not, stop and switch.

### 10.1 Settings billing tiles (free user)

- [ ] **10.1.1** Navigate to `/settings`. **Expect:** Account info card + Plan & billing card. The plan & billing card shows:
  - Header: "Plan & billing" + sub "You're on the Free plan · active." (no emerald "paid" badge — that's for paid plans only).
  - 3-cell mini stats: DOCUMENTS shows "{N} / 5" (the 5 is the free limit), VENDOR PORTAL = "Off", LLM SPEND MTD = "$0.00" (or whatever you've actually spent on extractions).
  - Three upgrade tiles: Pro ($49/mo, "Unlimited docs, all features."), Annual ($39/mo, "Same features, billed yearly.", highlighted with sky border), Founding ($39/mo, "Locked forever. First 50 only.").
- [ ] **10.1.2** **Don't expect:** to see "Manage billing" button (that's paid-user only).

### 10.2 Plan limit enforcement

- [ ] **10.2.1** Count your docs. If you're not at 5 yet, upload until you are.
- [ ] **10.2.2** Try to upload the 6th. **Expect:** toast **"Document limit of 5 reached. Upgrade to add more."** Row does NOT appear.
- [ ] **10.2.3** **Don't expect:** the upload to silently succeed then fail at extraction. The rejection is at upload time.

### 10.3 Stripe Checkout — Pro

- [ ] **10.3.1** Click **"Upgrade to Pro"**. **Expect:** button → "Redirecting…", page navigates away to Stripe Checkout (`checkout.stripe.com`).
- [ ] **10.3.2** Stripe page: shows CompliDrop branding (logo from your Stripe settings), product "CompliDrop Pro" or similar, price $49/month.
- [ ] **10.3.3** **Don't expect:** any reference to "monthly" plan id (legacy vocab — backend now rejects "monthly").
- [ ] **10.3.4** Email field is pre-filled with your QA Admin A email.
- [ ] **10.3.5** Card field: `4242 4242 4242 4242`, any future expiry, any 3-digit CVC, any zip.
- [ ] **10.3.6** Click "Subscribe". **Expect:** Stripe processes (1–2 seconds), then redirects back to `http://localhost:3000/settings?upgraded=true`.
- [ ] **10.3.7** On return: toast **"Welcome — you're now on a paid plan!"** Page refreshes settings.
- [ ] **10.3.8** **Expect (within ~5 seconds):** Plan & billing card now shows "You're on the Pro plan · active." + emerald **"paid"** badge. Documents tile no longer shows "/5". Vendor portal tile shows "On". The 3 upgrade tiles are replaced by a single primary **"Manage billing"** button.
- [ ] **10.3.9** **Don't expect:** an immediate update if the webhook hasn't fired — there can be a 2–5 second delay. If after 30 seconds the settings page still shows Free, force-refresh. If it still shows Free, the webhook didn't fire — log as a launch-blocker.

### 10.4 Receipt email (from Stripe, not from CompliDrop)

- [ ] **10.4.1** Check the QA Admin A inbox. **Expect:** within 1 minute, an email from Stripe with subject like "Your CompliDrop receipt" or "Payment receipt".
- [ ] **10.4.2** **Don't expect:** a CompliDrop-sent welcome-to-paid email (no such transactional email is wired in the MVP).

### 10.5 Free document limit no longer applies

- [ ] **10.5.1** Go to `/documents`. Upload more files until you've exceeded 5 total. **Expect:** uploads continue to work. No "Document limit" toast.

### 10.6 Customer portal

- [ ] **10.6.1** Back on `/settings`. Click **"Manage billing"**. **Expect:** button → "Redirecting…", navigates to Stripe customer portal.
- [ ] **10.6.2** Stripe portal shows the active subscription, payment method, billing history.
- [ ] **10.6.3** Test changing the card: add card `4000 0025 0000 3155`. Click through 3DS challenge. **Expect:** new card is set as default.
- [ ] **10.6.4** Test "Cancel plan". **Expect:** Stripe asks for confirmation, then the subscription is set to cancel at period end (not immediately). Stripe redirects back to `/settings` (or stays on the portal — verify the return URL).
- [ ] **10.6.5** Back on `/settings`. **Expect:** still shows Pro/active for now (cancels at period end). After the period ends and the `customer.subscription.deleted` webhook fires, the user should go back to Free.
- [ ] **10.6.6** **Don't expect:** any in-app dunning UI if the cancellation hasn't yet happened.

### 10.7 Failed-payment flow (optional, complex to test)

- [ ] **10.7.1** Create a new test customer in Stripe dashboard. Use card `4000 0000 0000 0341` (succeeds, then fails on renewal). Simulate a renewal — Stripe dashboard has a "send test webhook" for `invoice.payment_failed`.
- [ ] **10.7.2** **Expect:** subscription status flips to `past_due`. Settings page text changes to **"You're on the Pro plan · past_due."**
- [ ] **10.7.3** **Don't expect:** an in-app modal or banner. The only user signal is the status string.

### 10.8 Cancel checkout (user backs out)

- [ ] **10.8.1** As a fresh free user (or downgraded), click **"Upgrade to Annual"**. On the Stripe page, click the back arrow / close. **Expect:** redirect to `/settings?canceled=true`. Toast (info, not error): **"Checkout canceled — no changes made."**
- [ ] **10.8.2** Settings page still shows Free.

### 10.9 Founding plan

- [ ] **10.9.1** As a free user, click **"Upgrade to Founding"**. **Expect:** checkout works the same, $39/mo. After success, settings shows "You're on the Founding plan · active." with the emerald "paid" badge.

### 10.10 Unknown plan rejection

- [ ] **10.10.1** Open DevTools console on `/settings`. Run:
  ```js
  fetch('/api/billing/checkout', {method:'POST', headers:{'Content-Type':'application/json', 'Idempotency-Key': crypto.randomUUID()}, credentials:'include', body:JSON.stringify({plan:'monthly'})})
  ```
  **Expect:** 400 response, error code `billing.plan_unknown`, message **"Unknown plan. Expected one of: pro, annual, founding."** **Don't expect:** a Stripe session being created for an invalid plan.

### 10.11 Idempotency

- [ ] **10.11.1** Trigger two checkouts rapidly with the same Idempotency-Key (you'd need to manually fire two requests with the same key via DevTools). **Expect:** both return the same `sessionUrl`. (Hard to test by hand — log this as "verified by code only" if you skip.)

---

## §11 — Settings

Most of `/settings` was exercised in §10. A few extras:

### 11.1 Account info card

- [ ] **11.1.1** Verify: Organization shows "QA Admin A", Email shows your email, Role shows "admin", Time zone shows whatever you sent on registration (default `America/New_York`).
- [ ] **11.1.2** **Don't expect:** an edit button — there isn't one for org/email/role (verify this is intentional; if your QA finds users *want* to edit these, log as a post-launch enhancement).

### 11.2 Plan badge in sidebar

- [ ] **11.2.1** When on Pro: sidebar plan badge shows **"Pro"** (capitalized).
- [ ] **11.2.2** When on Founding: shows "Founding".
- [ ] **11.2.3** When on Free: shows "Free".

---

## §12 — Dashboard

### 12.1 KPI cards reflect real data

- [ ] **12.1.1** With multiple documents uploaded, verify the counts:
  - **Total documents** = number of non-deleted docs you've uploaded.
  - **Compliant** = number with `ComplianceStatus = Compliant`.
  - **Expiring ≤ 30d** = docs with `ComplianceStatus = ExpiringSoon`.
  - **Non-compliant** = docs with `ComplianceStatus = NonCompliant`.
- [ ] **12.1.2** Secondary row: **Vendors tracked** = vendor count. **Awaiting extraction** = docs currently in Pending or Processing. **Compliance rate** = compliant / total as percentage.

### 12.2 Expiry pipeline

- [ ] **12.2.1** Bars should be proportional. If you have 1 doc expiring in 7 days, the 0-30d bar should be visible. If you have 10 expired docs, the Expired bar should dominate.
- [ ] **12.2.2** **Don't expect:** bars to overflow the card or look broken with large counts.

### 12.3 Recent activity

- [ ] **12.3.1** Most recent 6 entries. Each shows a prettified action ("Document · Uploaded", "User · Logged in", "Vendor · Created", "Rule · Upserted", etc.) + a localized timestamp.
- [ ] **12.3.2** **Don't expect:** raw audit log codes ("document.uploaded") to leak through. **Don't expect:** any null/undefined entries.

### 12.4 Dashboard handles slow data

- [ ] **12.4.1** Stop the API. Refresh `/dashboard`. **Expect:** stats cards show zeros (silent degradation per the catalog) and activity card shows "Loading…" or fails silently. **Don't expect:** a hard crash or a "Bad Gateway" splash.
- [ ] **12.4.2** **Note:** the dashboard has no stale-data banner or retry button (per the catalog). This is a known gap — log as polish if it bothers you.

---

## §13 — Multi-tenancy isolation

The single most important security UX test. If this fails, **immediately stop and file launch-blocker**.

**Setup:** Browser B (different profile). Fresh, NOT logged in.

### 13.1 Register Admin B

- [ ] **13.1.1** In Browser B, navigate to `/register`. Register a new user:
  - Email: `ruben+qaB@gmail.com` (or whatever)
  - Company: "QA Admin B"
  - Password: `qa-launch-2026B`
- [ ] **13.1.2** Land on `/dashboard`. **Expect:** everything is empty (0 documents, 0 vendors, no activity except "User · Registered" and "User · Logged in").
- [ ] **13.1.3** **Critical:** the dashboard should show **"Welcome, Ruben"** (assuming the same first name) but org name "QA Admin B" in the sidebar. **Don't expect:** any of Admin A's data — counts, vendors, documents, recent activity.

### 13.2 Cross-org reads

- [ ] **13.2.1** Copy a document ID from Admin A's documents (Browser A → `/documents` → inspect a row URL → `/documents/{guid}`). In Browser B, navigate to that exact URL.
- [ ] **13.2.2** **Expect:** **"Document not found."** with the "Back to documents" link. **Don't expect:** the document to load OR a 403/Forbidden — the response must look identical to a truly-nonexistent document.
- [ ] **13.2.3** Same test with a vendor ID from Admin A → Admin B should see "Loading…" briefly then nothing or empty state (depending on routing). The vendor must be unreachable.

### 13.3 Cross-org writes

- [ ] **13.3.1** Try to edit Admin A's document via API. In Browser B's DevTools console:
  ```js
  fetch('/api/documents/{admin-A-doc-guid}/fields', {method:'PUT', headers:{'Content-Type':'application/json'}, credentials:'include', body:JSON.stringify({fields:[]})})
  ```
  **Expect:** 404 (the tenant filter pretends the doc doesn't exist). **Don't expect:** 200, 403, or any change to Admin A's data.

### 13.4 Vendor portal token tenant scope

- [ ] **13.4.1** As Admin B, create a vendor + generate a portal link. Note the token.
- [ ] **13.4.2** From Browser B (or any browser), open Admin B's portal link. **Expect:** "Hi {B's vendor}" + "{QA Admin B} asked for your latest…". Upload a file. **Expect:** the doc appears in Admin B's `/documents`, NOT in Admin A's.
- [ ] **13.4.3** Switch back to Browser A. Refresh `/documents`. **Expect:** the file you just uploaded via B's portal is NOT visible.

### 13.5 Recent activity isolation

- [ ] **13.5.1** Browser A `/dashboard` recent activity shows only A's events. Browser B's shows only B's. **Don't expect:** any cross-bleed.

### 13.6 Export isolation

- [ ] **13.6.1** Download Admin A's PDF audit report. Verify it contains ONLY A's documents.
- [ ] **13.6.2** Same for CSV.

### 13.7 Auth headers

- [ ] **13.7.1** In DevTools → Application → Cookies, Browser A's `cd_session` belongs to org A; Browser B's belongs to org B. Cookies are not shared (different browser profiles).

---

## §14 — Edge cases & error states

### 14.1 Long filenames

- [ ] **14.1.1** Upload a file named `this-is-an-absurdly-long-filename-that-should-not-break-the-layout-or-cause-any-truncation-issues-in-the-table-or-the-detail-page-header-2026.pdf`. **Expect:** table row truncates with ellipsis (or wraps cleanly). Detail page h1 wraps or shrinks but doesn't overflow.

### 14.2 Special chars in filenames

- [ ] **14.2.1** Upload `résumé&special—chars (2026).pdf`. **Expect:** filename displays correctly (no `&amp;`, no `%20`, no double-encoded chars). Download via "View file" yields the same filename.

### 14.3 Unicode in vendor name

- [ ] **14.3.1** Add a vendor named "Müller Glas & Söhne 株式会社". **Expect:** displays correctly in the table, in the document Vendor column, in the portal page ("Hi Müller Glas…"), in the reminder email.

### 14.4 Empty input edge cases

- [ ] **14.4.1** In the vendor edit form, blank out the Name and save. **Expect:** validation error OR server rejects with a friendly message. **Don't expect:** the vendor to be saved with an empty name.

### 14.5 Concurrent edits

- [ ] **14.5.1** Open a document detail page in two tabs (both Admin A). Edit a field in tab 1, save. Edit the same field in tab 2 (different value), save.
- [ ] **14.5.2** **Expect:** the second save wins (last-write-wins is acceptable for MVP). Refresh tab 1 — should show the new value. **Don't expect:** a 500 or a corrupted state.

### 14.6 Browser back button after submit

- [ ] **14.6.1** Submit the registration form. After landing on /dashboard, hit back. **Expect:** browser shows /register but the form may or may not be populated (browser-dependent). **Don't expect:** a duplicate-account error if you re-submit (server's idempotency or duplicate-email check should handle it).

### 14.7 Tab close mid-upload

- [ ] **14.7.1** Start an upload of a large file (~9 MB). Before the response, close the tab. **Expect:** the upload may complete server-side, OR may fail. Either way, no zombie state. Reopen the documents page — verify the doc is either present and processing, or absent.

### 14.8 5xx brown-out

- [ ] **14.8.1** Hard-kill the API mid-session (force-quit the process). Try to use the app.
- [ ] **14.8.2** **Expect:** every user action shows the generic fallback toast or stale-data banner. NEVER "Bad Gateway" or "TypeError". Console may have errors — that's OK as long as nothing user-visible shows internals.
- [ ] **14.8.3** Restart the API. **Expect:** subsequent actions work normally. No "stuck" UI state requiring full page refresh (though refresh is OK as a worst case).

### 14.9 Polling during DB suspended (Neon cold start)

- [ ] **14.9.1** On a staging deploy with Neon, leave the app idle for 6+ minutes (longer than Neon's 5-min suspend). Then load `/documents`.
- [ ] **14.9.2** **Expect:** first request takes 2–5 seconds (cold start). The UI shows loading state, then data. **Don't expect:** a perceived freeze, an error toast, or an empty state mistakenly shown before data loads.

### 14.10 Very old expired documents

- [ ] **14.10.1** Upload a doc whose `ExpirationDate` is in 2020 (manipulate the extracted date by editing fields after extraction). **Expect:** compliance badge = rose **"Expired"**. List page Expires column shows the date + "5y ago" or similar in rose text.

### 14.11 Very far future expiration

- [ ] **14.11.1** Upload a doc with expiration in 2099. **Expect:** sky/slate "in 26000d" or similar — no crash, no NaN.

---

## §15 — Accessibility & polish

### 15.1 Keyboard navigation

- [ ] **15.1.1** On `/dashboard`, press Tab repeatedly. **Expect:** focus moves visibly through sidebar links, then main content. Each focused element has a visible focus ring (browser default or app-styled).
- [ ] **15.1.2** Press Enter on a focused sidebar link. **Expect:** navigates.
- [ ] **15.1.3** Tab to the documents dropzone. Press Enter or Space. **Expect:** file picker opens.

### 15.2 Label association (project rule)

- [ ] **15.2.1** On `/register`, click the literal text "Full name" (not the input). **Expect:** focus moves to the input below. Same for every field.
- [ ] **15.2.2** Same test on `/login`, `/vendors/{id}`, `/rules` editor row.
- [ ] **15.2.3** If clicking a label doesn't focus the input, that's a `jsx-a11y/label-has-associated-control` violation — file a launch-blocker.

### 15.3 Screen reader spot-check

If you have a screen reader (NVDA on Windows is free):

- [ ] **15.3.1** Enable NVDA. Navigate to `/dashboard`. **Expect:** the page heading is announced as "Welcome, {name}" h1. Sidebar nav is announced as a list.
- [ ] **15.3.2** Tab to the stale-data banner (force one to appear by killing the API). **Expect:** it's announced as a status (`role="status"`) — `aria-live="polite"` means it announces without interrupting current speech.
- [ ] **15.3.3** Tab to a full error card (force a 5xx with no cached data). **Expect:** announced as alert (`role="alert"`).
- [ ] **15.3.4** On `/rules`, find a delete-rule trash icon. **Expect:** announced as "Delete rule, button" (the aria-label is set).

### 15.4 Color contrast

- [ ] **15.4.1** Use browser DevTools → Accessibility inspector. Check the contrast ratio for:
  - Sky-blue buttons on white background.
  - Slate-grey badges (Pending) on white.
  - Amber badges on white.
- [ ] **15.4.2** **Expect:** WCAG AA passes (4.5:1 for normal text, 3:1 for large text). Log any failures as launch-blocker.

### 15.5 Browser zoom

- [ ] **15.5.1** On any dashboard page, press Ctrl + (or Cmd +) to zoom to 150%. **Expect:** layout remains usable, nothing overflows, sidebar may collapse or shrink but stays functional.
- [ ] **15.5.2** Zoom to 200%. **Expect:** still readable. **Don't expect:** text to be cut off or buttons to disappear.

### 15.6 Responsive / mobile

Test the dashboard pages on a real phone (or DevTools mobile emulation):

- [ ] **15.6.1** `/dashboard` on iPhone-size viewport: sidebar collapses (or hamburger menu). KPI cards stack vertically. Pipeline bars are readable.
- [ ] **15.6.2** `/documents` on mobile: table is scrollable horizontally OR rows collapse into cards. Upload zone is tappable.
- [ ] **15.6.3** `/portal/{token}` on mobile (already tested in §6.9 — re-verify here).
- [ ] **15.6.4** `/login`, `/register` on mobile: form fields are properly sized, submit button is reachable above the keyboard.
- [ ] **15.6.5** **Don't expect:** any horizontal scroll on the page body (only inside scrollable elements like tables).

### 15.7 Cross-browser

Open the staging URL in each:

- [ ] **15.7.1** **Chrome** (your primary).
- [ ] **15.7.2** **Firefox**: anything noticeably different? Especially the dropzone (react-dropzone has been finicky in older Firefox).
- [ ] **15.7.3** **Safari** (if you have a Mac): the cookie + cross-origin setup is sometimes weird. Verify login + logout work.
- [ ] **15.7.4** **Edge**: identical to Chrome, but test anyway.

### 15.8 Print stylesheet

- [ ] **15.8.1** On `/dashboard`, Ctrl+P (Print). **Expect:** a printable representation (probably not styled — that's OK for MVP). **Don't expect:** broken pages or sidebar/buttons trying to print uselessly.

### 15.9 Visual consistency

Walk through the app and note any inconsistencies:

- [ ] **15.9.1** Are all "Save changes" buttons the same shape/color/position across forms?
- [ ] **15.9.2** Is the "active" badge style consistent (vendor portal links, system templates, etc.)?
- [ ] **15.9.3** Are status badge colors consistent for the same status across pages (e.g., "Completed" looks the same on list and detail)?
- [ ] **15.9.4** Are spacings / paddings consistent across cards?

### 15.10 Favicon & OG image

- [ ] **15.10.1** In any browser tab on CompliDrop pages, the favicon (water droplet) shows correctly. **Don't expect:** a default Next.js favicon.
- [ ] **15.10.2** Share the landing URL on Slack or Twitter (or paste into Slack as a test). **Expect:** OG image renders — the auto-generated OpenGraph image with the headline + price.

### 15.11 Loading transition smoothness

- [ ] **15.11.1** Navigate rapidly between sidebar items. **Expect:** smooth, no flashes of un-styled content (FOUC). The skeleton/loading states should be brief but visible.

---

## §16 — Known limitations (NOT bugs)

If you encounter any of these during testing, DO NOT file as bugs. They're either intentional MVP scope choices OR documented post-launch follow-ups. Just confirm the behavior matches the documented expectation.

### 16.1 No forgot-password flow

There is no `/api/auth/forgot-password` endpoint and no "Forgot password?" link on the login page. If a user forgets their password, the current recovery path is manual (you'd reset it via DB or by sending a password-reset link manually). Post-launch addition.

**What you might see:** users complaining. **What you should do:** file as a post-launch ticket in [#150](https://github.com/neboxdev/complidrop/issues/150), not as a bug.

### 16.2 No upload progress percentage

The dashboard dropzone and portal dropzone both show only an indeterminate "Uploading…" text. There's no progress bar or percentage. On a slow connection, a 9 MB file might appear to hang for 30–60 seconds.

**What you might see:** users pressing Cancel/refresh because it "looks frozen". **What you should do:** consider it polish for post-launch (add to [#150](https://github.com/neboxdev/complidrop/issues/150) if you want it).

### 16.3 Cost ceiling discovery is reactive

If an org hits its monthly extraction cost ceiling ($5 free, $50 paid), there's no proactive in-app warning. The user discovers it only when an upload's extraction lands in `Failed` state with the "Monthly extraction cost ceiling reached." error.

**What you might see:** a failed extraction on a doc that should have worked. Verify the error matches the message; if it does, this is expected behavior. **What you should do:** post-launch enhancement to add a banner at 80%, etc.

### 16.4 Session expiry doesn't auto-redirect

If both cookies expire, the user sees a `"Session expired. Please log in again."` toast on the next action but stays on the current URL. They have to manually click Log in (or any nav action that triggers another 401).

**What you might see:** a confused user re-clicking the same button. **What you should do:** noted for polish post-launch.

### 16.5 Single-vendor PDF export endpoint exists but has no UI

`GET /api/export/vendor/{id}` works at the API level but there's no button on `/export`. The single-vendor compliance package is unreachable via UI.

**What you should do:** decide pre-launch whether to surface it (it's a small frontend addition) or defer to post-launch.

### 16.6 No vendor-to-document assignment UI

When a doc is uploaded via the dashboard (not the vendor portal), there's no way in the UI to associate it with a vendor after the fact. The Vendor column shows `—` and the user can't fix that without uploading the doc through that vendor's portal link.

**What you might see:** users asking "how do I attach this doc to a vendor?". **What you should do:** decide pre-launch (small ticket: add a vendor dropdown to the detail page).

### 16.7 Toggling reminders has no feedback

On `/reminders`, flipping a toggle saves silently — no toast, no spinner. Users may wonder if it worked. This is intentional per the spec but could feel ambiguous.

### 16.8 Rule deletion has no confirmation

On `/rules`, clicking the trash icon on a rule deletes it immediately. No confirm dialog, no undo. Intentional but worth flagging if QA finds it surprising.

### 16.9 Vendor portal-link revoke has no confirmation

Same as 16.8 for revoke.

### 16.10 Dashboard has no stale-data banner

If `/dashboard`'s data fetch fails, the cards silently show zeros — no error card, no retry button. (Documents/Vendors pages DO have the banner; dashboard doesn't yet.)

### 16.11 Founding plan is auth-only

You won't see Founding on the landing page or via `?plan=founding`. It's only offered to logged-in users on `/settings`. Per [ADR 0011](../adr/0011-plan-vocab-unified-with-founding-as-authenticated-only-promo.md).

---

## §17 — Performance & feel

Not formal load testing — just perceived responsiveness.

### 17.1 Time to interactive

- [ ] **17.1.1** Open `/` in an incognito tab. Watch DevTools → Network → DOMContentLoaded. **Expect:** < 2 seconds on a good connection.
- [ ] **17.1.2** First click on any CTA should respond within ~100 ms. **Don't expect:** a noticeable delay between click and visual response.

### 17.2 Dashboard cold load

- [ ] **17.2.1** Log in fresh (or after a Neon cold-start). Land on `/dashboard`. **Expect:** the initial render is visible within 2 seconds, with stats populating shortly after.

### 17.3 Polling overhead

- [ ] **17.3.1** With one document in Pending/Processing, watch DevTools → Network. **Expect:** roughly one request every 3 seconds to `/api/documents/{id}` (detail page) or every 5s to `/api/documents` (list page).
- [ ] **17.3.2** **Don't expect:** continuous network traffic after all docs are in terminal state.

### 17.4 Many documents

- [ ] **17.4.1** Upload all 25 from `stress/25-mixed-docs/`. **Expect:** all rows appear in the table, queue processes them sequentially (each ~30–60s extraction). Table remains responsive even with 25+ rows.
- [ ] **17.4.2** **Don't expect:** the page to slow noticeably with 25 rows. (Pagination kicks in if > pageSize, but the default is 25, so this is the boundary.)

### 17.5 Bundle size sanity

- [ ] **17.5.1** Open DevTools → Network. Reload the landing page. **Expect:** total transferred under ~500 KB on first load (Next.js + Tailwind + a few components). If it's > 2 MB, log a finding.

### 17.6 Memory leaks

- [ ] **17.6.1** Open `/documents` with several Processing docs. Let it poll for 5+ minutes. **Expect:** memory usage in DevTools → Memory remains stable, doesn't grow linearly.

---

## §18 — Sign-off checklist

You're done when this entire table is filled in.

| Section | Status | Bugs found | Critical bugs? | Notes |
|---|---|---|---|---|
| §0 Setup | [ ] | | | |
| §1 Smoke | [ ] | | | |
| §2 First-time user | [ ] | | | |
| §3 Auth | [ ] | | | |
| §4 Documents | [ ] | | | |
| §5 Vendors | [ ] | | | |
| §6 Portal | [ ] | | | |
| §7 Rules | [ ] | | | |
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
- [ ] Sentry, UptimeRobot, Resend domain auth are live in production.
- [ ] Production env vars set, `ValidateOnStart()` boots cleanly.

### Post-QA cleanup

- [ ] Delete the test Stripe customers from the Stripe Dashboard test mode list.
- [ ] Delete the test orgs from staging DB (or note them as "qa" orgs for future regression).
- [ ] Archive the fixtures folder OR keep it for next regression pass.
- [ ] Update [`WORKLOG.md`](../../WORKLOG.md) with a short summary of what QA caught.

---

## Appendix — quick re-test list after a bug fix

When a bug is fixed mid-launch-prep, you don't need to re-run the whole plan. Run:

1. The exact failing step from the plan.
2. The 2–3 adjacent steps in the same section (regression sweep).
3. §1 smoke (5 min) to confirm nothing else broke.
4. §13 multi-tenancy (5 min) to confirm no isolation regression.

That's it. Full plan re-run only on major architectural changes.
