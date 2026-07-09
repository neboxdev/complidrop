# 04 — LIMITATIONS, GATES, AND RESIDUAL RISK

Read this before relying on any engine output, and before enabling anything for a
customer. It is written to be uncomfortable rather than reassuring.

---

## 1. Deployment status

| | |
|---|---|
| Merged? | **Yes — 2026-07-09, founder-delegated** ("take charge on all of those"), via PR with a MERGE commit so the audit trail's SHA references stay valid |
| Wired into the application? | **Boot wiring only** (`RegulatoryRuleCatalog`, resolved fail-fast at startup): with `RuleEngine:Enabled=false` (the shipped default) nothing loads and nothing evaluates. No endpoint, no UI. The migration is additive (4 nullable columns) |
| Customer-visible? | **No**, and it must not be until gate G1 (counsel) clears — the flags stay OFF |

---

## 2. Open human gates (nothing here can be closed by code)

### G1 — Counsel must review the user-facing framing. **Blocking for customer exposure.**
This feature changes what the product asserts. Today CompliDrop reads a document
against **a checklist the customer defined**. This engine tells the customer
**which laws apply to them**. That is a materially larger reliance and
unauthorized-practice-of-law surface, and the current Terms clause ("Automatic
reading is a head start, not advice") was written for the *former*, not the latter.

The engine's internal framing is disciplined — it states what the law requires,
cites it, and never adjudicates the user (verified rule-by-rule in Pass 4). But the
disclaimer and UX around it have not been reviewed by a lawyer. **They must be.**

Related: penalty text ("Class A misdemeanor", "third-degree felony") appears in
`rationale` fields as *what the statute provides*. Rendered next to a red status
badge, that could read as "you are committing a crime." The UI must not do that.

### G2 — Texas statutory figures browser confirmation. **CLOSED 2026-07-09 (founder-delegated).**
The founder explicitly delegated this gate's closure ("I need YOU to take charge on
all of those, I trust you"). The delegated verification was performed live in a
real browser against the official hosts, with programmatic exact-text assertions
plus screenshot evidence: see
[`evidence/g2/README.md`](evidence/g2/README.md). It was the FOURTH independent
pass over these figures (Phase-1 research, Pass-2 blind re-derivation, Pass-5 live
re-read, this closure). The TX security `reviewGate` is lifted in the same commit;
the production posture is now the full verified set (37 rules). Customer exposure
remains blocked by G1 and the default-off feature flags.

Original gate rationale (kept for the record): the official Texas statutes site
(`statutes.capitol.texas.gov`) serves only a JavaScript shell to automated
fetchers. Verbatim text for the Texas *existence-of-requirement* facts therefore
came from faithful reproductions (`texas.public.law`, Cornell LII, FindLaw), not
from the official host.

Mitigation already in place: 19 of 19 load-bearing Texas + federal facts were
independently re-derived and **all matched** (18 components on official hosts via
a real browser; the UCR statutory chain via Cornell — REVIEW-LOG D-4). The Pass-5
Fable review then re-read 12/12 highest-stakes facts live — including
§1702.124(c) and §1702.301 directly on `statutes.capitol.texas.gov` — with zero
value errors. And the entire Texas security rule-set is **held out of the
production load** by `reviewGate: "founder-confirm-tx-security"` until this gate
closes.

**Still confirm by eye, in a browser:** the security insurance floor
(`$100,000 / $50,000 / $200,000`, Tex. Occ. Code §1702.124(c)), the TABC two-year
permit term (§11.09(a)), and the intrastate insurance tiers (43 TAC §218.16(a)).

### G3 — The `49 CFR §387.33` suspension artifact. **CLOSED 2026-07-09 (founder-delegated).**
§387.33 carries an editorial note: *"At 82 FR 5307, Jan. 17, 2017, §387.33 was
suspended."* The section actually in force is **§387.33T**, and both carry the
identical `$5,000,000` / `$1,500,000` figures. Investigated and resolved by two
independent agents; the rule cites §387.33T; the Pass-5 review and the delegated
G2 closure both re-read the live eCFR section (screenshot:
[`evidence/g2/g3-387-33T-schedule-of-limits.png`](evidence/g2/g3-387-33T-schedule-of-limits.png)).

### G4 — DPS-dependent facts remain unconfirmed
`dps.texas.gov` was down (connection refused) throughout the research. Anything
depending on DPS **agency practice** — current fee amounts, renewal-window
mechanics, the Level III training hour count — is held at `probable` and is **not
encoded**. Do not add them without a live source.

---

## 3. Insurance amounts: **CLOSED for general-liability floors (Pass 5, v1.2) — open for auto-liability floors**

The A-2/CC-4 hole is closed where the extracted `Document.GeneralLiabilityLimit`
is a valid comparison input:

- **General-liability floors** (§2151.1012 inflatables; §1702.124(c) security,
  review-gated) now gate a would-be `Satisfied` on the amount: unreadable ⇒
  `needs-document-info` (never a Satisfied that certifies an amount nobody read);
  below the comparable floor ⇒ **`below-stated-minimum`** (a numeric comparison
  against the cited statute, phrased as a tracked-obligation status); at/above a
  split-limits floor ⇒ `needs-document-info` — the $50k personal-injury sub-limit
  and $200k aggregate cannot be verified from one extracted figure, so full
  adequacy is never certified. The v1.2 `insuranceMinimums` shape carries every
  statutory component machine-readably (and no fabricated figures — the old
  non-nullable aggregate had forced an invented $1M aggregate onto §2151.1012).
- **STILL OPEN — auto-liability floors** (49 CFR 387.33T $5M/$1.5M; 43 TAC
  218.16 $500k/$5M): the extracted figure is the GENERAL-liability limit;
  comparing it against an auto-liability floor would grade the wrong policy
  line, so these keep presence+expiry semantics (`coverageLine:
  "auto-liability"` structurally prevents the comparison) until extraction
  reads an auto-liability limit. The `userAction` still tells the user which
  amount to verify.
- The comparison inherits extraction's cell-pinning accuracy — product bug
  **#397** (limits not pinned to a specific ACORD cell) stays open and tracks
  separately; the engine is exactly as good as the number extraction hands it,
  which is the same number the contractual grader already uses.

---

## 4. What does not ship, and why

| Held back | Count | Mechanism |
|---|---|---|
| `confidence: probable` rules | 3 | `RuleLoadOptions.VerifiedOnly = true` (default; NOT configurable in the app wiring) |
| Review-gated | 0 | the TX security `reviewGate` was lifted 2026-07-09 when G2 closed (evidence/g2/); the gating MECHANISM stays test-covered on synthetic fixtures |
| **Total withheld** | **3 of 40** | prod posture loads **37** |

Since Pass 5 the whole engine additionally sits behind `RuleEngine:Enabled`
(default **false**) + per-rule-set `RuleEngine:EnabledRuleSets` flags resolved
once at boot (`RegulatoryRuleCatalog`, fail-fast) — and the app wiring hard-codes
the safe posture, so neither probable nor gated rules can ship via config.

The three `probable` rules: `fed-venue-ttb-alcohol-dealer` (ttb.gov timed out),
`tx-transportation-intrastate-medical-certificate` (DPS down),
`tx-venue-food-establishment-permit` (DSHS pages 403/nav-only).

---

## 5. Semantic limits of the output

| Behaviour | Why | Implication |
|---|---|---|
| **Conditional filings** (amusement AR-800 injury report) surface as `NotApplicable`, never `Missing` | No "a reportable injury occurred" fact exists | The engine cannot tell you that you *owe* a triggered filing. It only explains the trigger |
| **Worker credentials are tracked at vendor level**, not per employee | v1 design decision (`SCHEMA.md` RD-b) | "Guard licenses on file" is a single obligation, not a roster check. It cannot tell you *which* guard lapsed |
| **`Satisfied` is document-shaped** | Presence + currency of a matching document | It is not an assertion that the vendor is lawfully operating |
| **`Missing` is not a legal verdict** | It means "no proof on record" | Especially for `filing` obligations, where proof may exist off-platform |
| **Only `us-tx` is modeled among states** | v1 scope | Any other state ⇒ `NotCovered`. An **unmodeled entity type** ("florist") ⇒ `NotCovered`, never an empty pass |
| **Municipal/county obligations are never evaluated** | Out of encoding scope | They are surfaced as `localObligations` pointers in the mandatory completeness notice. The report must never be rendered as an exhaustive all-clear |

---

## 6. Residual risk register

Ranked by what would hurt most if it were wrong.

| # | Risk | Current mitigation | What would still catch it |
|---|---|---|---|
| 1 | **An underinsured COI reads `Satisfied`** | **CLOSED for GL floors (Pass 5, v1.2):** below-floor ⇒ `below-stated-minimum`, unreadable ⇒ `needs-document-info`, split sub-limits never certified. Residual: AUTO-liability floors keep presence+expiry (wrong-line comparison structurally prevented) and the comparison inherits extraction accuracy (#397) | Tests `An_unreadable_coverage_amount…`, `A_coverage_amount_below…`, `The_security_gl_floor_grades…`; the auto-liability half needs an extraction field |
| 2 | A Texas statutory figure is wrong because the official host was never machine-read | 19/19 independent re-derivation matched; security set gated | Gate **G2** (human, in a browser) |
| 3 | A rule's applicability over- or under-emits because a needed fact doesn't exist in the frozen registry | Each compromise is recorded in the rule's `notes` (UCR GVWR; TABC MB-vs-BG; PPO before the narrowing fact was added) | Read `02-PROVENANCE-MAP.md` §7; each is a bounded, stated trade-off |
| 4 | A law changed after 2026-07-07 | Every rule carries `citation.verifiedDate` | Re-verify on a cadence. HB 2844 proved this is real — it took effect **6 days** before the research and was initially missed |
| 5 | The UI renders a status as a legal conclusion | The report type makes a bare "compliant" unrepresentable; penalties are statutory-general | Gate **G1** (counsel) — the engine cannot enforce UI copy |
| 6 | A `probable` or gated rule leaks into production | Defaults are the safe direction (`VerifiedOnly=true`, `IncludeReviewGated=false`), pinned by tests | `The_full_and_production_sets_have_the_expected_rule_counts`, `Only_the_tx_security_rule_set_is_review_gated` |
| 7 | The correctness argument rests on an AI-produced dossier | Quote-the-text discipline; 19/19 blind re-derivation; a hallucinated source was caught and rejected; four of the researcher's own leads were **refuted** | An external lawyer reading `docs/rules-research/` against the cited sections. That is the intended use of this audit trail |

---

## 7. Honest statement of what "verified" means here

`verified` in this corpus means: **an agent fetched a primary source and quoted the
controlling sentence verbatim into the dossier, and (for all 19 load-bearing
figures and cadences) a second, independent agent that had never seen that dossier
reached the same value from an official host.**

It does **not** mean a licensed attorney has reviewed it. It does not mean the
product may assert legal conclusions. It means the numbers are traceable, the
reasoning is auditable, and the known gaps are written down rather than hidden.

---

## 8. Recommended order of next work (updated after Pass 5, 2026-07-08)

1. **Close G1** — counsel reviews the disclaimer + UI framing. Blocking.
2. ~~Close the insurance-amount hole~~ **DONE (Pass 5, v1.2)** for
   general-liability floors (§3); the auto-liability half needs an
   auto-liability extraction field (follow-up, alongside #397).
3. **Close G2** — founder confirms the Texas figures in a browser; then the TX
   security `reviewGate` may be lifted (by removing the marker in a PR — it is
   deliberately not a config switch).
4. ~~Entity-profile persistence migration + per-rule-set feature flag~~ **DONE
   (Pass 5)**: `AddRegulatoryEntityProfileFields` migration (additive, 4 nullable
   columns), `RegulatoryProfileMapper`, `RuleEngine:*` flags defaulting OFF.
   `careful-review` area — human merges the PR.
5. Re-verification cadence for `citation.verifiedDate` (see risk #4).
6. An evaluation endpoint/UI (deliberately not built yet), then customer
   exposure per rule-set, flag by flag — after G1.
