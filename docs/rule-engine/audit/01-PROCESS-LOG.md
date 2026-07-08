# 01 — PROCESS LOG: every step, in order

Chronological account of how the rule engine was produced, what decided each
turn, and who/what performed and verified each step. Dates are 2026-07-07 unless
stated. Cross-references point at the artifact that proves the claim.

**Roles used below:**
- **Orchestrator** = the main session. Wrote the specs, made design decisions,
  independently verified builds/tests, spot-checked numbers against sources.
- **Subagent** = an isolated agent context spawned for one task (research,
  encoding, review, fixes). Subagents cannot see each other's work — this
  isolation is what makes the re-derivation and review passes meaningful.

---

## Step 0 — Codebase grounding (before any question was asked)

Read the existing compliance model so the scoping interview would be concrete:
- `api/CompliDrop.Api/Entities/Compliance.cs` — existing `ComplianceTemplate` /
  `ComplianceRule` (per-org **contractual** checklists).
- `api/CompliDrop.Api/Services/ComplianceCheckService.cs` — the existing grader.
- `frontend/src/app/terms/page.tsx` — the current disclaimer posture.

**Finding that shaped everything:** the existing system grades an *uploaded
document* against *a checklist the customer defined*. The new engine answers a
different question — *which documents does this entity need **by law***. It is a
separate, upstream layer, not a rewrite. Recorded in `docs/rule-engine/SCHEMA.md`
§ "Codebase fit".

Also swept for out-of-scope regulation: **no Spain/EU content existed** in the
repo (only product-privacy GDPR mentions). Verifiable: see README §4 command 4.

---

## Step 1 — Scoping (Phase 0), founder-approved

Four questions asked, four answers locked:

| Question | Decision |
|---|---|
| Jurisdictions | **Federal + Texas** |
| Whose obligations | **Both** vendor entity types **and** the venue org itself |
| Categories | Licenses & permits, worker certifications, **insurance where law-mandated** (regulatory vs contractual marked explicitly). W-9/tax deferred |
| Rule storage | **Versioned in-repo data files** (diffable, reviewable, testable — not DB rows) |

Six entity types followed: `caterer`, `event-rental`, `security-service`,
`transportation`, `photographer-videographer`, `venue-org`.

---

## Step 2 — Methodology written BEFORE research

`docs/rules-research/METHODOLOGY.md` was authored first, so the research had a
contract to satisfy rather than being judged after the fact. It fixed:
- **No rule without a primary source** (eCFR/US Code/GPO, Texas statutes, or the
  responsible `.gov` agency). Secondary sources may guide, never cite.
- **Confidence is first-class:** `verified | probable | uncertain`. Only
  `verified` ships.
- **Absence is a finding.** "No state license exists for X" is a sourced entry, so
  the AUDIT TRAIL can distinguish *no obligation* from *not researched*. (Precision
  note, Pass 5 / UNVER-30: absences are dossier-only — nothing absence-shaped loads
  into the ENGINE, whose report stays silent about unresearched areas beyond the
  mandatory completeness notice. The distinction is documentation-level in v1.)
- **Regulatory vs contractual** must be marked on every insurance item.
- Municipal/county obligations are **noted, not encoded** (they later became the
  engine's `localObligations` → completeness notice).

---

## Step 3 — First research attempt FAILED (model exhaustion)

Six research subagents (one per entity type) were launched. **All six terminated
mid-research** on a model usage limit, **before any wrote a file**. Disk state
afterwards: only `METHODOLOGY.md`.

This is recorded rather than hidden because it drove two changes:
1. **Durable incremental writes.** Every later research agent writes its file
   early with a `**Status:** PARTIAL|COMPLETE` header and updates as it goes, so
   an interruption leaves labelled, usable progress instead of nothing.
2. It exposed a planning assumption: the original brief tiered work between a
   frontier model (rule derivation) and a cheaper model (engine code). That tier
   split could no longer hold.

---

## Step 4 — The pivot, and the controls that replaced the lost safeguard

The founder directed that **all** work — including rule derivation, previously
reserved for the strongest model — proceed on the available model, and suggested
duplicating the review.

The original safeguard was *"a frontier model derives the rules."* That safeguard
was gone. Rather than proceed on assurances, it was replaced with **process- and
redundancy-level controls that do not depend on model tier** (recorded in
`docs/HANDOFF.md` § "GOVERNING DECISION" and `METHODOLOGY.md`):

| Control | What it does |
|---|---|
| **Quote-the-text** | A rule may be `verified` **only if** the researcher fetched the source and quoted the controlling sentence **verbatim** in an `Operative text:` field. No quote ⇒ downgrade. |
| **Full independent re-derivation** | Raised from the brief's "≥30% sample" to **100% of load-bearing facts**, performed by agents that never saw the first dossier, then diffed. |
| **Confidence conservatism** | Any ambiguity or unfetchable source ⇒ `probable`, which does not ship. |
| **Provenance tiers** | `official` / `reproduction-validated` / `secondary`, recorded per rule. |
| **Founder human-gate** | Rule content requires sign-off; the Texas statutory figures require a human browser confirmation (the official site blocks automation). |

**Auditor's note:** the honest reading is that the correctness argument rests on
*quoted primary text plus independent re-derivation*, not on model capability.
Section 03 explains how to test that claim yourself.

---

## Step 5 — Research (6 subagents, isolated contexts)

Each agent researched one entity type across federal + Texas, writing
`docs/rules-research/{federal,texas}/<entity>.md` to the methodology's entry
format. Each was given **leads explicitly labelled as hypotheses to verify or
refute**, never as facts to copy.

**Evidence the research corrected rather than confirmed its priors — four leads
were refuted:**

| Lead given | What the research found | Where |
|---|---|---|
| Texas amusement rides administered by TDLR | **TDI** (Dept. of Insurance) — TDLR has no role | `texas/event-rental.md` |
| Food-handler training due 60 days after hire | **30 days** (25 TAC §228.31(d)) | `texas/caterer.md` |
| TABC consolidation was 2021 HB 1545 | HB 1545 passed **2019**; two-year terms phased from Sept 2021 | `texas/venue-org.md` |
| Texas intrastate insurance in 43 TAC §218.13 | **§218.16** (§218.13 is the application section) | `texas/transportation.md` |

**A hallucinated source was caught and rejected.** During the HB 2844 refresh, a
flat-fetch summarizer invented section numbers and a false "county health
authority" clause. The agent detected the mismatch against the verbatim official
text and discarded it. This is the quote-the-text control working as designed.

### Source-access reality (discovered, then exploited)
| Host | Behaviour | Consequence |
|---|---|---|
| `statutes.capitol.texas.gov` | JS SPA — serves only a shell to WebFetch **and** curl | Initially forced reproduction sources |
| `ecfr.gov` | Anti-bot 302 redirect | Fell back to **govinfo.gov (GPO, official)** |
| `web.archive.org` | Blocked by tooling | Wayback not an option |
| `dps.texas.gov` | Down (ECONNREFUSED) | DPS agency-practice facts held at `probable` |
| **Playwright browser** | **Renders both SPAs** | **Official text became reachable** — later used to upgrade provenance and to re-derive |

---

## Step 6 — Orchestrator pre-review of the dossier (Pass 0)

The orchestrator read the completed dossier files (not the agents' summaries) and
found two systematic issues, recorded in `REVIEW-LOG.md`:
- **F-1/F-2:** several `verified` rules rested on a **single** reproduction host —
  which satisfies quote-the-text but not the ≥2-source provenance bar.
- **F-3:** one cadence (amusement filing) had a genuine statute-vs-agency tension.

Dossier completed: **12 files, ~57 obligations, 50 `verified` / 7 `probable` / 0
`uncertain`.**

---

## Step 7 — Review gauntlet on the RULE CONTENT (before any code)

### Pass 1 — Legal / compliance reviewer
Verdict: sound **as research**; **not** safe to surface to end users without
counsel review of the framing. Confirmed: framing states the law and cites it
(never "you are compliant"); regulatory-vs-contractual labelling correct;
applicability tightly gated; absences correctly bounded; **zero non-US content**.

It found one substantive research defect and several provenance overstatements:
- **L-1 (fixed):** **Texas HB 2844**, effective **2026-07-01** — *six days before
  the research* — created a statewide DSHS **Mobile Food Vendor** license (new
  H&S ch. 437B). The caterer dossier still framed food trucks as a local-only
  matter. A dedicated refresh agent verified the bill against **two** official
  sources (codified ch. 437B via Playwright + the enrolled bill PDF) and added
  `OBL-TX-CATERER-007` with a **narrow** applicability gate: it applies only to a
  caterer that serves from a food-vending vehicle, **not** to a caterer who
  transports food and plates it in the venue kitchen.
- **L-2/L-3/L-4:** provenance labels overstating single-reproduction reads.

### Pass 2 — Independent re-derivation (the core correctness control)
Two subagents, **blind to the dossier's conclusions**, were asked to *find* the
load-bearing values themselves from official sources (using Playwright to reach
the previously-unreachable official hosts), then their answers were diffed
against the dossier.

- **19 of 19 load-bearing facts matched** (8 money + 11 cadence/threshold).
  **Zero value errors.** Full table: `REVIEW-LOG.md` § Pass 2.
- Because every reproduction-sourced number that was independently checked proved
  correct, the reproduction hosts were treated as *validated* — which is why
  single-reproduction *existence* facts are labelled
  `verified / reproduction-validated` rather than downgraded. The Texas statutory
  set still requires the human browser confirmation (gate **G2**).

**It also produced five corrections — this is what the pass is for:**

| ID | Correction |
|---|---|
| **D-1** | The inflatable-insurance rule cited the **general** Class B amusement-ride limit. The **specific, controlling** statute is **Tex. Occ. Code §2151.1012** — `$1,000,000` per-occurrence combined single limit for continuous-airflow bounce houses. Rule rewritten; confidence upgraded `probable → verified` (official read). |
| **D-2** | The operative federal transport-insurance section is **49 CFR §387.33T** (§387.33 was suspended, 82 FR 5307). Amounts unchanged. |
| **D-3** | The Texas intrastate `$5M` tier reads **"27 or more people, including the driver"** (current 43 TAC §218.16(a), 2024 amendment). The dossier had quoted the **superseded pre-2024** "26+ not including the driver" phrasing. Confirmed independently by **both** re-derivation agents. |
| **D-4** | The UCR ">10 passengers" threshold traces to **49 U.S.C. §31101(1)(B)**, not 49 CFR 390.5 (which uses a different 8/15 split). |
| **D-5** | The DOT medical 24-month cap lives in **§391.45(b)** (§391.43 states no numeric cap). |

All five were applied to the dossier and re-verified.

---

## Step 8 — Founder checkpoint

`RULES-REVIEW.md` was compiled and presented: the full rule table with sources and
confidence, the pass-2 discrepancy log, coverage summary, and the open gates. The
founder approved proceeding ("go"), which was read as: commit the deliverable and
build the engine. It was **explicitly not** read as clearing gate **G1** (counsel
on the user-facing framing) or **G2** (browser spot-confirm) — both gate *customer
exposure*, not *building*.

→ **Commit `6931d35`** (dossier, methodology, schema draft, review log, RULES-REVIEW).

---

## Step 9 — Schema frozen

`docs/rule-engine/SCHEMA.md` moved DRAFT → **FROZEN v1**:
- The **applicability-fact registry** was locked against the dossier's actual
  triggers (16 facts). Notably `maxPassengerSeatingCapacity` is defined as
  **seats including the driver**, so a single fact serves every transport
  threshold (CDL ≥16, fed $1.5M ≤15, TX reg >15, TX $5M ≥27, UCR >10).
- Three design questions resolved: filings tracked as cadence-driven obligations
  with optional proof (RD-a); worker credentials tracked at **vendor** level in v1,
  not per-employee (RD-b); `documentType` mapped onto the **existing** extraction
  vocabulary (RD-c).
- The three legal-review requirements were written in as **hard** engine
  requirements.

---

## Step 10 — Engine core built, then verified by the orchestrator

A subagent built the engine strictly to the frozen schema, using **synthetic**
fixtures only (fake `test-widget` entity), so the *mechanics* were proven
independently of any real legal content.

The orchestrator then ran the build and suite itself: **110 tests passed.** The
three legal requirements were confirmed to be enforced **structurally** — most
importantly, `ObligationReport` exposes **no** `IsCompliant` boolean, so a bare
"compliant" is not representable.

---

## Step 11 — Real rules encoded, then spot-checked against the sources

A subagent translated the verified dossier into
`api/CompliDrop.Api/RuleData/{us-fed,us-tx}/*.json` — **translation only**, no
re-derivation. Result: **39 rules** (36 `verified` + 3 `probable`) in 10 files,
plus golden-file tests.

The orchestrator independently re-ran the suite (**110 tests**) and read the
highest-stakes JSON directly against the dossier:
- Security GL: `perOccurrence 100000 / aggregate 200000`, with the `$50,000`
  personal-injury sub-limit correctly carried in the rationale (the schema has one
  `perOccurrence` field).
- Federal transport: `$5M` (≥16 seats, interstate) / `$1.5M` (≤15, interstate).
- Texas intrastate: `$5M` (≥27) / `$500k` (16–26), gated on `operatesInterstate=false`.
- CDL and DOT-medical correctly **not** interstate-gated; the Texas CDL carries
  `satisfiesFederal` so the federal CDL is not double-emitted.

→ **Commit `087c6e1`** (engine + rule data + 110 tests).

---

## Step 12 — Adversarial + code review on the ENGINE (Passes 3 & 4)

Four independent reviewer subagents ran against the committed engine.

**What they cleared** (two passes independently certified the core correct):
Kleene combinators, cadence date-math (leap day, month-end clamp, grace, DST-
irrelevance via `DateOnly`), version `validFrom`/`validTo` selection, the capacity
partitions at their exact boundaries, `satisfiesFederal` gating, engine purity,
framing, confidence honesty, and **every number matched the dossier — no drift**.

**What they found** (all fixed — see `REVIEW-LOG.md` for the full list with
file:line evidence):

| ID | Finding | Why it mattered |
|---|---|---|
| **A-1** | A matched document with **no readable expiry** fell through to `Satisfied` — forever | A COI whose expiry extraction failed would silently certify as current. The exact false-pass-is-liability failure. |
| **A-3 / C-1** | Entity type wrong in **both** directions: unset ⇒ over-asserted `Missing` obligations from ignorance; set-but-unmodeled ("florist") ⇒ silent empty all-clear | Violated "never assert from ignorance" and the completeness rule |
| **CC-1** | The event-conditional AR-800 injury report showed `Missing` for **every** inflatable renter | Asserted a gap the vendor did not owe |
| **CC-2** | The FMCSA Clearinghouse was gated interstate-only, but it attaches **intrastate too** | Silently **dropped a verified obligation** for Texas intrastate carriers |
| **CC-3** | The PPO (bodyguard) credential emitted for every armed-guard company | Over-obligation |
| **A-2 / CC-4** | Insurance **dollar amounts are never compared** — any COI satisfies a `$5M` floor | Scoped by design (v1 = presence + expiry) but the label overclaimed → now documented as a v1 limitation, see `04-…` |
| **A-5 / CC-8** | The Texas security rule-set shipped under the verified-only filter, conflating *confidence* with the *methodology human-gate* | The set that most needs G2 was not held back |
| **T-1 / T-2 / T-3** | Test gaps that let real mutations survive: `satisfiesFederal` suppression was only tested at Kleene-**False** (never **Unknown**); the insurance divergence was only tested at 20 seats (never at 15/16/26/27; the fed `$1.5M` and TX `$5M` floors had **zero** coverage); the unset-state branch was untested | A bug could have shipped green |

Plus A-4 (`obligationRef` collisions), A-6/CC-6 (UCR over-emission), A-7 (brittle
state matching), CC-5 (DWC-005 missing the employee condition), CC-7 (local
obligations never surfaced).

---

## Step 13 — Consolidated fix pass, then verified again

All design decisions were locked by the orchestrator first (no rule content
changed), then implemented and re-verified:

- New `ObligationStatus.NeedsDocumentInfo` — a matched document whose currency
  can't be determined **can never read `Satisfied`** on a renewing obligation.
  (A held **one-time** credential still reads `Satisfied` — that's correct.)
- Entity type handled both ways: unset ⇒ `NeedsProfileInfo`; unmodeled ⇒
  `NotCovered`. Never a bare all-clear, never an assertion from ignorance.
- Engine honours `cadence.kind = conditional-filing`.
- Clearinghouse re-gated to for-hire + ≥16 seats (interstate leaf removed).
- New fact `providesArmedCloseProtection` narrows the PPO gate.
- Rule-set `reviewGate` marker + `RuleLoadOptions` defaults flipped to
  `VerifiedOnly=true, IncludeReviewGated=false`. **The TX security set is now held
  back independently of confidence.** Prod posture = **31 rules**.
- `localObligations` metadata now populates the completeness notice per entity.
- **+44 tests**, including regressions for every finding above and the three
  mutation-style gaps.

Orchestrator verification (not taken on trust): build clean; **154 passed / 0
failed**; data gates re-read directly (Clearinghouse now capacity-only; UCR gained
`≥11`; the insurance tiers unchanged).

→ **Commit `c53a975`.**

---

## Step 14 — What was deliberately NOT done

- **Not merged, not pushed, not deployed.** The work described here sits in commits 6931d35 / 087c6e1 / c53a975 (plus the audit-trail commit carrying this document, and the Pass-5 commit) on
  `feat/compliance-rule-engine`.
- **Not wired into the app.** The engine is inert: no DI/boot registration, no
  endpoint, no UI. Zero runtime behaviour change.
- **Insurance amount enforcement** — deliberately out of v1 scope, documented as
  the top follow-up (needs a coverage-amount extraction field).
- **Entity-profile persistence migration + per-rule-set feature flag** — the next
  build step; it touches DB schema (a `careful-review` area).
- **Gates G1 (counsel) and G2 (browser spot-confirm)** remain open. See
  `04-LIMITATIONS-AND-GATES.md`.
