# 04 — LIMITATIONS, GATES, AND RESIDUAL RISK

Read this before relying on any engine output, and before enabling anything for a
customer. It is written to be uncomfortable rather than reassuring.

---

## 1. Deployment status

| | |
|---|---|
| Merged? | **No.** Three commits on `feat/compliance-rule-engine` (`6931d35`, `087c6e1`, `c53a975`) |
| Pushed / PR? | **No** |
| Wired into the application? | **No.** The engine is inert — no DI registration, no boot loading, no endpoint, no UI. Zero runtime behaviour change |
| Customer-visible? | **No**, and it must not be until gates G1–G2 clear |

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

### G2 — Founder must confirm the Texas statutory figures in a browser. **Blocking for enabling TX rule-sets.**
The official Texas statutes site (`statutes.capitol.texas.gov`) serves only a
JavaScript shell to automated fetchers. Verbatim text for the Texas *existence-of-
requirement* facts therefore came from faithful reproductions (`texas.public.law`,
Cornell LII, FindLaw), not from the official host.

Mitigation already in place: 19 of 19 load-bearing Texas + federal facts were
independently re-derived from **official** hosts (via a real browser) and **all
matched**. And the entire Texas security rule-set is **held out of the production
load** by `reviewGate: "founder-confirm-tx-security"` until this gate closes.

**Still confirm by eye, in a browser:** the security insurance floor
(`$100,000 / $50,000 / $200,000`, Tex. Occ. Code §1702.124(c)), the TABC two-year
permit term (§11.09(a)), and the intrastate insurance tiers (43 TAC §218.16(a)).

### G3 — A human should eyeball the `49 CFR §387.33` suspension artifact
§387.33 carries an editorial note: *"At 82 FR 5307, Jan. 17, 2017, §387.33 was
suspended."* The section actually in force is **§387.33T**, and both carry the
identical `$5,000,000` / `$1,500,000` figures. This was investigated and resolved
by two independent agents, and the rule cites §387.33T. It is still an unusual
regulatory shape worth one human look before the `$5M` figure drives a verdict.

### G4 — DPS-dependent facts remain unconfirmed
`dps.texas.gov` was down (connection refused) throughout the research. Anything
depending on DPS **agency practice** — current fee amounts, renewal-window
mechanics, the Level III training hour count — is held at `probable` and is **not
encoded**. Do not add them without a live source.

---

## 3. The most important limitation: insurance **amounts are not checked**

> For an `insurance` obligation, `Satisfied` means **"a certificate of the right
> type is on file and unexpired."** It does **not** mean the coverage amount meets
> the statutory floor.

- The floor is carried (`insuranceMinimums`) and stated in the user-facing
  `userAction` ("verify the amount meets $X"), but **the engine never compares it**.
- `IDocumentLike` has no coverage-amount field; per `SCHEMA.md` §6, v1 satisfaction
  is presence + expiry. Numeric limit comparison is the *contractual* grader's
  existing job.
- **Concretely:** a security company that uploads a valid, unexpired COI with
  `$50,000` limits will show `Satisfied` against an obligation whose own text
  asserts a `$100,000` floor.

This was found by two independent reviewers (adversarial A-2, compliance-claims
CC-4) and is documented in `SCHEMA.md` § "v1 KNOWN LIMITATIONS". It is **the top
follow-up** and must be closed before any insurance verdict is customer-facing.
Closing it requires (a) a coverage-amount field sourced from extraction, and (b) a
richer `insuranceMinimums` shape — the current single `perOccurrence` field cannot
even represent the security rule's three statutory limits.

Related open product bug: **#397** (liability limits not pinned to a specific ACORD
cell).

---

## 4. What does not ship, and why

| Held back | Count | Mechanism |
|---|---|---|
| `confidence: probable` rules | 3 | `RuleLoadOptions.VerifiedOnly = true` (default) |
| Review-gated (all TX security) | 5 | `reviewGate` + `IncludeReviewGated = false` (default) |
| **Total withheld** | **8 of 39** | prod posture loads **31** |

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
| 1 | **An underinsured COI reads `Satisfied`** | Documented; `userAction` tells the user to verify the amount | Nothing in code. **This is the open hole.** Close it before customer exposure |
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

## 8. Recommended order of next work

1. **Close G1** — counsel reviews the disclaimer + UI framing. Blocking.
2. **Close the insurance-amount hole** (§3) — coverage-amount extraction field +
   `insuranceMinimums` comparison, before any insurance verdict is shown.
3. **Close G2** — founder confirms the Texas figures in a browser; then the TX
   security `reviewGate` may be lifted.
4. Entity-profile persistence migration + per-rule-set feature flag (DB-schema
   change; `careful-review` area). The flags must default **OFF**.
5. Re-verification cadence for `citation.verifiedDate` (see risk #4).
6. Only then: expose anything to a customer, per rule-set, flag by flag.
