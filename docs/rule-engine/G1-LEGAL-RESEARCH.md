# G1 Legal Research Memorandum — regulatory rule engine, user-facing framing

> **NON-ATTORNEY WORK PRODUCT — NOT LEGAL ADVICE.** Researched and written by an
> AI system (Claude) on 2026-07-09/10 at the founder's direction, because counsel
> is not yet affordable and the feature will not ship until counsel reviews this
> work. It is structured for a licensed Texas attorney to **validate rather than
> research from scratch**: every Texas statute quoted was read live on
> `statutes.capitol.texas.gov` during preparation; the pivotal cases were
> verified against fetched opinion text; every other authority is tagged with
> its verification tier in the Appendix. **Gate G1 remains OPEN until a licensed
> attorney signs off.** The feature remains disabled (`RuleEngine:Enabled=false`,
> no endpoint, no UI) until then.

---

## I. Questions presented

1. **UPL / reliance.** Does presenting sourced, per-obligation regulatory
   requirements to Texas businesses constitute or approach the unauthorized
   practice of law, or create negligent-misrepresentation / reliance exposure —
   and what disclaimer language, placement, and prominence are required?
2. **Terms of Service.** Does the existing "head start, not advice" clause need
   amendment for this feature?
3. **Status wording.** Are the status labels (`missing`, `satisfied`,
   `below-stated-minimum`, …) acceptable?
4. **Penalty text.** May statutory penalty references appear in obligation
   rationales, and under what constraints?
5. **E&O posture.** What must the Terms say about currency of legal content,
   and what insurance should be in place before enabling?

## II. Short answers

1. **UPL: LOW risk under an on-point statutory safe harbor, conditioned on one
   design requirement.** Tex. Gov't Code §81.101(c) — enacted in 1999
   specifically to reverse a software-UPL injunction — excludes from the
   practice of law internet-distributed software "if the products clearly and
   conspicuously state that the products are not a substitute for the advice of
   an attorney." The statutory sentence must appear conspicuously **in the
   product**, on every report and export — not only in the Terms. Keep the
   engine **fully automated**: the LegalZoom cases teach that human staff
   applying the rules to a specific customer's situation is what converts
   protected software into UPL exposure, and Texas's pre-1999 case law
   (*Cortez*) condemned exactly that human-advisor pattern.
   **Reliance: MANAGEABLE.** Negligent misrepresentation (Restatement §552)
   fits this product — it is "in the business of supplying information," and
   good faith is no defense, only reasonable care. But damages are limited to
   pecuniary loss; the plaintiff class is limited to known recipients; and
   *McCamish* expressly endorses disclaimers as the mechanism for information
   suppliers to avoid liability, while *Orca Assets* negates justifiable
   reliance **as a matter of law** where written terms directly contradict the
   claimed reliance. The mandatory per-report notice is therefore doctrinally
   load-bearing — with the honest caveat that *Orca* involved sophisticated
   parties, so the notices must be plain-English and unmissable, not legalese.
2. **Yes.** §V.2 contains the full rider. Critical drafting rule from *Italian
   Cowboy* (verified verbatim): a merger clause "does not disclaim reliance" —
   only **clear and unequivocal disclaimer-of-reliance language** does, so the
   rider contains an express no-reliance covenant (the Mosey pattern), not just
   integration boilerplate. What Terms **cannot** do (all verified live):
   waive the DTPA (§17.42 requires the consumer to have had independent counsel
   at purchase — impossible in self-serve signup); exclude your customers from
   the DTPA (§17.45(4): businesses under $25M in assets are "consumers"); or
   cap liability for DTPA misrepresentation claims (*Arthur's Garage*: a
   contractual cap was enforced against negligence/contract/warranty claims
   but **reversed as applied to DTPA misrepresentation**). Warranty-predicated
   DTPA claims are the exception — express warranties are contractually created
   and can be disclaimed/limited (*Southwestern Bell v. FDP Corp.* line), which
   is why the all-caps no-warranty clause still does real work.
3. **Yes, with adjustments** (§V.3): render `below-stated-minimum` as **"Below
   stated minimum (as read)"** with a mandatory sub-caption; never render
   `missing` as "you lack a legally required X" (it means "no matching document
   on file in CompliDrop"); make non-coverage and can't-verify states visually
   distinct from verified states (the FTC's *TRUSTe* action treats implying a
   verification you didn't run as deceptive).
4. **Yes, under the constraints already implemented plus one** (§V.3): penalty
   sentences stay statutory-general, never adjacent to a red badge, and
   collapsed behind a "What the statute provides" affordance by default.
5. **Bundle tech E&O + cyber (~$1,000–4,000/yr at $1M limits, market data)
   before enabling**, with four placement checks in §IV.F — above all, describe
   the regulatory-content feature accurately in the application and confirm how
   the policy treats a *customer's fine* claimed as damages. The Terms must
   disclaim currency of legal content and disclose per-rule verification dates
   (the product already displays them).

## III. Facts

CompliDrop's rule engine (merged 2026-07-09; feature flags OFF; no endpoint or
UI) evaluates a customer's entity profile against 40 encoded US federal + Texas
rules (37 in the production posture). Framing discipline enforced structurally
and pinned by automated tests: no overall "compliant" verdict exists; every
report carries a mandatory non-exhaustiveness notice; rationales state what the
law requires and cite it; statuses describe the tracked-document record. Every
verified rule traces to a primary source with a verbatim statutory quote and a
verification date (audit trail: `docs/rule-engine/audit/`). Customers: Texas
small businesses (event venues), $49/month, self-serve signup. All human
support is deliberately generic; no employee reviews a specific customer's
documents against the law.

## IV. Analysis

### A. Unauthorized practice of law

**Texas is simultaneously the historically harshest and the best-protected UPL
jurisdiction for this product.** The pre-1999 baseline was severe:
*Unauthorized Practice Comm. v. Cortez*, 692 S.W.2d 47 (Tex. 1985) held that a
lay service that interviewed customers and **selected which government forms
their situation required** was practicing law as a matter of law, and *Fadia v.
UPLC*, 830 S.W.2d 162 (Tex. App.—Dallas 1992, writ denied) enjoined a
mass-market self-help will manual, expressly rejecting the permissive national
line and saying change "must come from the legislature." On that baseline, the
N.D. Tex. permanently enjoined Quicken Family Lawyer statewide (Jan. 1999).

**The Legislature answered within months.** H.B. 1507 (76th Leg. 1999) added
§81.101(c) — *verified live 2026-07-09; screenshot at
`audit/evidence/g1/g1-gv-81-101c-software-carveout.png`*:

> "In this chapter, the 'practice of law' does not include the design,
> creation, publication, distribution, display, or sale, **including
> publication, distribution, display, or sale by means of an Internet web
> site**, of written materials, books, forms, **computer software, or similar
> products if the products clearly and conspicuously state that the products
> are not a substitute for the advice of an attorney.**"

The Fifth Circuit then vacated the Parsons injunction outright: "We therefore
VACATE the injunction and judgment … in light of the amended statute."
*UPLC v. Parsons Tech., Inc.*, 179 F.3d 956 (5th Cir. 1999) (per curiam)
(*verified against reporter text*). Texas thus has an on-point statutory safe
harbor enacted specifically to protect disclaimed self-help software —
including interactive software (Quicken Family Lawyer selected documents based
on user answers), and including internet delivery by the statute's own words.

**The design principle from the LegalZoom cases: automation is the shield;
human intervention is the exposure.** *Janson v. LegalZoom.com*, 802 F. Supp.
2d 1053 (W.D. Mo. 2011) denied LegalZoom summary judgment *because* its
"document preparation service goes beyond self-help because of the role played
by its human employees," while accepting that self-help products are lawful and
that computer delivery "does not change the essence" either way. North
Carolina's dispute ended in a 2015 consent judgment (lawyer-reviewed templates
+ disclosure that forms are no substitute for an attorney), later codified
(N.C. Gen. Stat. §84-2.2). **Operating rule for CompliDrop:** the moment staff
review a specific customer's documents and tell *that customer* what the law
requires of them, *Janson* — and in Texas, *Cortez* — is squarely implicated.
Support answers stay generic; the engine stays automated end-to-end.

**The general-information line.** No authority found squarely condemns — or
blesses — a compliance checklist / license-requirement database as such (a
negative finding, stated as such). The national doctrine (*Dacey*, N.Y. 1967:
publishing "what the law is" to the public is not practice; *Brumbaugh*, Fla.
1978: selling generalized legal explanations is permitted, answering a specific
customer's questions about which filings *their* situation requires is not)
draws the line at individualized application. CompliDrop's per-profile matching
is individualized **by software** — which is exactly the category §81.101(c)
was enacted to protect, so in Texas the statutory safe harbor, not the
general-information doctrine, is the load-bearing defense. *Cortez* is the case
counsel most needs to distinguish: Mrs. Cortez was a **human advisor** in a
relationship of trust; CompliDrop is a disclaimed software product — a
distinction the DOJ/FTC framework (practice of law = specialized legal skill
applied within "a client relationship of trust or reliance") supports. Payroll,
HR-compliance, and registered-agent companies have escaped UPL claims for
decades by staying on this side of the line; no reported UPL decision against
such a provider was located.

**"Clearly and conspicuously."** No reported construction in the UPL context,
but Texas has a developed general standard to borrow: Tex. Bus. & Com. Code
§1.201(b)(10) (conspicuous = "so written, displayed, or presented that a
reasonable person against which it is to operate ought to have noticed it,"
with capitals-heading and contrasting-type examples; a question of law for the
court); *Dresser* (fair notice must "attract the attention of a reasonable
person" on the face of the document); and the DTPA's own concrete benchmark
(§17.42(c): bold-face ≥ 10 pt under a required heading). **Translation:** the
statutory sentence appears on every report and export, in type at least as
prominent as body text, under a distinct heading or in contrasting style —
never only in the Terms. That comfortably exceeds what the Parsons-era product
offered.

**Chapter 83 proviso.** §81.101(c) does not authorize violating Gov't Code
ch. 83 — the prohibition on non-attorneys charging to prepare "a legal
instrument affecting title to real property" (§83.001, *verified live*).
CompliDrop prepares no instruments; categorically inapplicable.

**Residual risk.** *Cortez* asserts inherent judicial power to define the
practice of law beyond the statute (§81.101(b) preserves it), and the safe
harbor has essentially no construing case law because it ended the litigation
that produced it. Risk grade with §V implemented: **LOW** for this product
shape. Re-evaluate before ever (a) preparing documents or filings for
customers, (b) recommending legal courses of action, (c) adding any human
review of a specific customer's obligations, or (d) adding a free-form legal
Q&A surface (e.g., an LLM chat) — each is a different legal animal.

### B. Civil liability for wrong output

**Negligent misrepresentation is the realistic theory, and it fits.** Texas
adopted Restatement (Second) of Torts §552 in *Federal Land Bank Ass'n v.
Sloane*, 825 S.W.2d 439 (Tex. 1991): liability for one who, in the course of
business, "supplies false information for the guidance of others in their
business transactions," if the supplier failed to exercise reasonable care and
the plaintiff justifiably relied with pecuniary loss. A compliance-requirements
SaaS is "in the business of supplying information" in a way most defendants are
not — assume the duty exists. Good faith is **no defense** (*D.S.A., Inc. v.
Hillsboro ISD*, 973 S.W.2d 662 (Tex. 1998)); only reasonable care is. Three
structural limits:

1. **Pecuniary loss only** (§552B; *Sloane*; *D.S.A.*): out-of-pocket and
   consequential damages, never benefit-of-the-bargain. Worst case ≈ a
   customer's actual fine/loss traceable to reliance.
2. **Known party, known purpose** (*McCamish, Martin, Brown & Loeffler v. F.E.
   Appling Interests*, 991 S.W.2d 787 (Tex. 1999)): subscribers (and arguably
   their portal vendors), not the world.
3. **Justifiable reliance can be defeated ex ante — by design.** *McCamish*
   expressly says an information supplier "may avoid or minimize the risk of
   liability … by setting forth (1) limitations as to whom the representation
   is directed … or (2) disclaimers as to the scope and accuracy" of the
   underlying work. *JPMorgan Chase v. Orca Assets*, 546 S.W.3d 648 (Tex.
   2018) (*verified*): "red flags" **or** direct contradiction between the
   claimed reliance and "the express, unambiguous terms of a written
   agreement" negate justifiable reliance **as a matter of law** (following
   *Grant Thornton v. Prospect High Income Fund*, 314 S.W.3d 913 (Tex. 2010)).
   The engine's mandatory per-report notice is that direct contradiction, ON
   THE SAME SCREEN as the output. **Honest caveat for counsel:** *Orca* leaned
   on the sophistication of parties in a $3.2M deal; a court may hesitate to
   negate reliance as a matter of law against a small venue owner at $49/mo —
   which argues for plain-English, unmissable notices rather than fine print.

**The economic loss rule narrows, but does not eliminate, the tort.**
*LAN/STV v. Martin K. Eby Constr.*, 435 S.W.3d 234 (Tex. 2014) (the common law
"restrict[s] recovery of purely economic damages" and prefers "allocating some
economic risks by contract rather than by law"); *D.S.A.* requires an injury
independent of the contract between contracting parties. But *Sharyland Water
Supply v. City of Alton*, 354 S.W.3d 407 (Tex. 2011) — with the verifier's
precision note — described the rule as having been applied in defective-product
and failure-to-perform-contract cases while listing negligent misrepresentation
among torts where purely economic losses remain recoverable, and declined to
resolve the rule's full reach. Net: the subscriber's wrong-output claim is
strongly pulled into contract (where the §V.2 stack governs), with §552 as the
surviving tort-side theory (where the reliance-negating notices govern).

**Products liability — the chart problem, stated honestly.** *Winter v. G.P.
Putnam's Sons*, 938 F.2d 1033 (9th Cir. 1991) (*verified against reporter
text*) declined "to expand products liability law to embrace the ideas and
expression in a book" and found publishers have "no duty to investigate the
accuracy of the contents" — but expressly distinguished **aeronautical
charts** as "highly technical tools," which courts have treated as *products*
(*Saloomey v. Jeppesen*, 707 F.2d 671 (2d Cir. 1983); *Brocklesby v. United
States*, 767 F.2d 1288 (9th Cir. 1985); *Aetna Cas. & Sur. v. Jeppesen*, 642
F.2d 339 (9th Cir. 1981)), and noted computer software "may" also qualify. The
chart cases are structurally alarming here: *Aetna* located the defect in the
**translation layer** — Jeppesen took accurate government data and rendered a
display whose defect made it dangerous, and "it was reliance on this graphic
portrayal that Jeppesen invited"; *Brocklesby* held upstream-source accuracy
does not immunize the downstream translator. A rule engine does what Jeppesen
did: it converts accurate primary sources into an instantly readable status
display engineered to be relied on without reading the statute.
Counterweights: (a) **harm profile** — the chart cases involved death;
a wrong compliance verdict causes purely economic loss, where Texas's economic
loss rule blocks defective-product strict liability and returns the claim to
§552/contract; (b) **the modern algorithm cases** — *Rodgers v. Christie*, 795
F. App'x 878 (3d Cir. 2020) (non-precedential): a risk-estimation algorithm is
not a "product" because "information, guidance, ideas, and recommendations"
are not products; while *Garcia v. Character Technologies* (M.D. Fla. 2025)
shows plaintiffs now recharacterize *the app* (not the ideas in it) as the
defective product. Bottom line for counsel: strict products liability for the
engine's output is **unlikely in Texas** given the purely-economic harm, but
the framing is not frivolous post-*Garcia*; the design already leans book-like
in the ways that matter (visible statutory citations inviting verification;
explicit non-exhaustiveness; verify-with-the-authority instructions), and
negligent misrepresentation remains the exposure that actually matters.

**What actually moves this risk: care in the rule content.** No disclaimer
excuses carelessness under §552. The audit trail — primary-source citations
with verbatim quotes, independent verification passes, verified-only shipping,
per-rule verification dates, the planned re-verification cadence (quarterly,
plus after each biennial Texas legislative session) — is the reasonable-care
evidence, and should be presented to counsel and any future adversary as such.

### C. Contract & DTPA architecture

**Enforceability of the contract stack (Texas is B2B-enforcement-friendly).**
- *Limitation of liability*: *Bombardier Aerospace v. SPEP Aircraft Holdings*,
  572 S.W.3d 213 (Tex. 2019) enforced clauses excluding punitive/consequential
  damages even after a fraud finding, on Texas's "strongly embedded public
  policy favoring freedom of contract"; *Arthur's Garage v. Racal-Chubb*, 997
  S.W.2d 803 (Tex. App.—Dallas 1999) upheld even a $350 cap against
  negligence/contract/warranty claims. (Both involved commercial parties;
  *Bombardier* stressed counsel-represented sophistication — expect somewhat
  less deference for adhesion SaaS terms, hence conspicuous presentation.)
- *Fair notice scope*: *Dresser* limits the express-negligence + conspicuousness
  doctrine to clauses that exculpate a party's OWN future negligence
  (releases/indemnities); an ordinary LoL cap or informational disclaimer is
  not automatically subject to it — but the cheap, high-value move is to
  present the cap, warranty disclaimer, and notices conspicuously anyway
  (§1.201(b)(10) formatting), mooting the question. Actual knowledge is an
  escape hatch (*Storage & Processors v. Reyes*, 134 S.W.3d 190 (Tex. 2004)).
- *Online assent*: Texas enforces clickwrap under ordinary contract principles
  (*Barnett v. Network Solutions*, 38 S.W.3d 200 (Tex. App.—Eastland 2001);
  *In re Online Travel Co.*, 953 F. Supp. 2d 713 (N.D. Tex. 2013): a
  purchase-blocking assent flow is enforceable; no scroll-through required;
  "the central issue is whether … users were conspicuously presented with the
  agreement prior to entering into a contract" — browsewrap is the losing
  pattern). *Aerotek v. Boyd*, 624 S.W.3d 199 (Tex. 2021): under the Texas
  UETA, attribution is provable by the security procedure — **so require an
  affirmative click adjacent to a conspicuous terms link at signup AND retain
  per-user acceptance logs (user, timestamp, terms version).**
- *Reliance disclaimer drafting*: *Italian Cowboy Partners v. Prudential*, 341
  S.W.3d 323 (Tex. 2011) (*verified verbatim from the opinion*): contract
  language that "amounts to a standard merger clause … does not disclaim
  reliance," and even intended disclaimers fail unless done "by clear and
  unequivocal language." The §V.2 rider therefore contains an express,
  first-person no-reliance covenant, not integration boilerplate. (*Prudential
  v. Jefferson Associates*, 896 S.W.2d 156 (Tex. 1995) — as-is clauses negate
  causation when freely negotiated between capable parties, with fraud and
  boilerplate/adhesion caveats — workflow-sourced; counsel to confirm its
  weight for a $49/mo adhesion signup, which sits at the weak end of its
  factors.)

**The DTPA overlay (all core sections verified live 2026-07-09/10):**
- **No waiver** (§17.42: requires the consumer to have been represented by
  independent counsel at acquisition — unattainable; don't draft one).
- **Your customers are consumers** (§17.45(4): businesses under $25M assets).
- **Large-transaction exemptions unavailable** (§17.49(f): >$100k with counsel;
  §17.49(g): >$500k).
- **Caps don't stop DTPA misrepresentation.** *Arthur's Garage* enforced its
  cap for negligence/contract/warranty but **reversed it as applied to DTPA
  misrepresentation** — and *Weitzel v. Barnes*, 691 S.W.2d 598 (Tex. 1985)
  holds affirmative misrepresentations actionable under the DTPA
  notwithstanding contrary written terms. Post-1995, §17.50(a)(1)(B) adds a
  reliance element to laundry-list claims, which the conspicuous notices
  temper — but the only reliable defense is **not making false affirmative
  representations**, in marketing or in the product UI (which is part of the
  representation — see §IV.D).
- **The warranty exception**: warranty-predicated DTPA claims can be limited
  because express warranties are contractually created (*Arthur's Garage*,
  following *Southwestern Bell v. FDP Corp.*) — the all-caps no-warranty
  clause has real, enforceable work to do.
- **Stakes**: treble economic damages for "knowing" conduct (§17.50(b)(1),
  verified); AG enforcement under §17.47 with civil penalties up to $10,000
  per violation (verified) — and each customer-facing misstatement can be a
  separate violation.

### D. Regulator exposure (FTC §5 / Texas AG)

Under FTC Act §5, deception is a representation "likely to mislead the
consumer acting reasonably in the circumstances, to the consumer's detriment,"
express claims are presumptively material, and the **prior-substantiation
doctrine** requires a reasonable basis for objective claims *before*
dissemination — with required rigor scaling with the consequences of a false
claim (Deception and Substantiation Policy Statements — canonical FTC
documents; quotes workflow-sourced, Appendix tier C). The FTC enforces for
small-business victims of B2B services (*FTC v. FLEETCOR*, N.D. Ga.; summary
judgment for the FTC in 2022 — tier C).

**Three actions map almost one-to-one onto this product's risk surface:**
- ***Henry Schein Practice Solutions* (2016; $250,000 — verified on ftc.gov):**
  software vendor charged for marketing that its product provided
  industry-standard encryption and *"ensured that practices using its software
  would protect patient data, as required by [HIPAA]."* The FTC treats "helps
  ensure regulatory compliance" as an objective, policed representation — not
  puffery. **Never market CompliDrop as ensuring or guaranteeing compliance.**
- ***TRUSTe* (2014 — tier C):** representing a verification process you do not
  actually run is deceptive. **Product mapping:** a status must never imply a
  check that was not performed — unparseable documents, disabled rule sets,
  and out-of-scope obligations must be visually distinct non-verification
  states (the engine's `needs-document-info` / `NotCovered` semantics already
  do this; the UI must preserve it).
- ***SkyMed* (2020 — tier C):** displaying a compliance seal implying
  third-party review that never happened is actionable — caution for any
  badge-like UI element.

The **puffery line** (5th Cir.): vague boasting is non-actionable; a claim
that is "specific and measurable," capable of empirical verification, is
actionable — and a vague slogan becomes actionable when specific claims give
it concrete meaning (*Pizza Hut v. Papa John's*, 227 F.3d 489 (5th Cir. 2000);
*Presidio Enterprises*, 784 F.2d 674 (5th Cir. 1986) — tier C). Because the
product's per-obligation statuses are themselves specific representations,
**the app UI is part of the advertising claim**, read together with marketing
copy. A disclaimer cannot cure a false affirmative status; deception is judged
on net impression. The rule-data provenance map doubles as the
**substantiation file** the FTC doctrine asks for.

### E. Comparable-product practice (fetched from live pages)

Every comparable product uses the same **three-layer architecture**, and the
closest analogs are the most aggressive:

1. *Status disclaimer* — what the company is not: **Harbor Compliance**
   (*verified live by the author, 2026-07-10*): a "Not Professional Advice"
   section — "Nothing on our Website is intended to be and does not constitute
   professional advice, whether legal, financial, accounting, or otherwise,
   and no such professional relationship is formed between you and us" — plus
   a "Reliance on Information Posted" section — "We do not warrant the
   accuracy, completeness, or usefulness of this information. Any reliance you
   place on such information is at your own risk." LegalZoom: top-of-terms +
   every-page-footer "not a law firm" (tier C).
2. *No-reliance / recharacterization*: Mosey ToS §7.6 — customer agrees it
   "will not rely on any information provided … as legal or other professional
   advice"; Drata recasts outputs as "RECOMMENDATIONS ONLY … DO NOT CONSTITUTE
   ANY WARRANTY OR GUARANTY THAT CUSTOMER … WILL BE FULLY COMPLIANT"; Vanta:
   "ONLY TOOLS FOR ASSISTING CUSTOMER" (tier C — re-pull before filing).
3. *Responsibility allocation*: customer is "solely responsible" for its own
   compliance and for consulting qualified professionals (Avalara, ADP,
   Vanta, Drata — tier C).

Placement is as consistent as substance: dedicated conspicuously-headed
sections in the customer agreement, all-caps for the compliance-outcome
disclaimer, plus a persistent short-form notice at the point of consumption.
CompliDrop's per-report notice already implements the point-of-consumption
layer; §V.2 adds the contract layers.

### F. Insurance (tech E&O) — market research, tier C throughout

Technology E&O / MPL responds to the wrong-output claim ("error, omission,
misstatement … in rendering technology services"). Four placement checks:

1. **Describe the product accurately at application** — coverage is scoped to
   the declared services; a compliance-information product described as
   generic document software invites a coverage fight. Ask for a custom
   professional-services definition naming the regulatory-content feature.
2. **Confirm the treatment of a customer's FINE claimed as damages** — forms
   diverge (some exclude fines/penalties without a third-party carve-back;
   some exclude only fines imposed directly on the insured). This is the
   highest-value definitional item to negotiate.
3. **Exclusions to check**: no specimen reviewed had a literal "UPL exclusion"
   (a genuine negative finding), but watch (a) services-by-an-attorney
   laundry-list exclusions, (b) regulatory-action exclusions (an AG/UPLC
   proceeding against CompliDrop itself would likely be uninsured), (c)
   criminal-acts exclusions (UPL can be criminal), and (d) warranty/guarantee
   exclusions — **any "guaranteed compliance" language would move claims
   outside coverage**, aligning insurance incentives with §IV.D.
4. **Underwriting hygiene**: applications ask what share of customer contracts
   contain consequential-damages disclaimers, fee-tied caps, and warranty
   disclaimers, and answers are incorporated into the policy — the §V.2 stack
   is also an insurability requirement. Cyber must be bundled (standalone
   cyber forms exclude professional-services claims).

Indicative market pricing for a sub-$1M-revenue SaaS: roughly **$1,000–4,000 /
year bundled tech E&O + cyber at $1M limits** (published marketplace figures:
Insureon ~$110/mo tech E&O average; Embroker $500–1,500/yr small-business E&O;
Vouch startup medians ~$3.7k E&O; broker-published $2.5–6k combined). $1M is a
sensible floor (published average MPL claim severity ≈ $227k).

## V. Deliverables

### V.1 Product-surface disclaimer (the §81.101(c) instrument)

Rendered on **every** obligation report, export, and any screen presenting
rule-engine output — conspicuous per §1.201(b)(10) (distinct heading or
contrasting style, ≥ body-text size, before or immediately adjacent to the
obligations list; also printed on page 1 of every PDF export):

> **NOT LEGAL ADVICE — NOT A SUBSTITUTE FOR AN ATTORNEY.** CompliDrop is a
> software product. It is **not a substitute for the advice of an attorney**,
> and using it does not create an attorney–client or other professional
> relationship. This report lists only the regulatory obligations CompliDrop
> tracks for the profile and documents you provided, based on published
> federal and Texas sources last verified on the dates shown with each item.
> It is **not a complete list of your legal obligations**, laws change, and
> local (city or county) requirements are not included. Statuses describe the
> documents on record in CompliDrop — they are not determinations that you or
> your vendor has complied with or violated any law. Verify each item with
> the cited source or a licensed attorney.

(The first sentence carries the exact §81.101(c) formula. The existing
engine-level `CompletenessNotice.DefaultText` should be updated to this text
when the UI is built — one string constant, already test-pinned as mandatory.)

### V.2 Terms of Service rider (full draft for counsel markup)

> **Regulatory Obligation Tracking.**
> (a) *Not legal advice; not a substitute for an attorney.* The Service
> includes features that list licenses, permits, filings, certifications, and
> insurance minimums that may apply to your business or your vendors
> ("Regulatory Content"), with citations to the published source and the date
> we last verified it. Regulatory Content and the Service are **not a
> substitute for the advice of an attorney**, are not legal, insurance, tax,
> or professional advice, and do not create any attorney–client or other
> professional relationship between you and CompliDrop.
> (b) *No reliance.* You agree that Regulatory Content is general information
> assembled by software; that you **will not rely on Regulatory Content or on
> any status shown in the Service as legal advice or as a determination of
> your or any vendor's legal compliance**; and that you will independently
> verify any requirement with the cited authority or a qualified professional
> before acting on it. You acknowledge that CompliDrop has expressly told you
> not to rely on Regulatory Content in this way and that this paragraph is a
> material part of our bargain.
> (c) *Statuses describe documents, not compliance.* A status such as
> "satisfied," "missing," or "below stated minimum (as read)" describes the
> documents on record in CompliDrop as read by automated extraction — it is
> not a statement that you or any vendor has complied with or violated any
> law, and extraction may misread a document.
> (d) *Currency.* Laws change. Regulatory Content reflects sources as of the
> per-item verification date shown in the Service. CompliDrop DOES NOT WARRANT
> THAT REGULATORY CONTENT IS ACCURATE, COMPLETE, OR CURRENT, and has no duty
> to update it on any schedule.
> (e) *Responsibility.* You are solely responsible for your own and your
> vendors' compliance with law, for the decisions you make using the Service,
> and for consulting a licensed attorney about your legal obligations.
> (f) *No guarantee.* USE OF THE SERVICE DOES NOT ENSURE OR GUARANTEE
> COMPLIANCE WITH ANY LAW.
>
> **Limitation of Liability** *(feature-agnostic; replaces/joins the existing
> clause)*: TO THE MAXIMUM EXTENT PERMITTED BY LAW, COMPLIDROP'S AGGREGATE
> LIABILITY ARISING OUT OF OR RELATING TO THE SERVICE IS LIMITED TO THE
> AMOUNTS YOU PAID FOR THE SERVICE IN THE TWELVE (12) MONTHS BEFORE THE EVENT
> GIVING RISE TO THE CLAIM, AND COMPLIDROP IS NOT LIABLE FOR ANY INDIRECT,
> INCIDENTAL, SPECIAL, CONSEQUENTIAL, OR EXEMPLARY DAMAGES, INCLUDING FINES,
> PENALTIES, LOST PROFITS, OR LOST BUSINESS, EVEN IF ADVISED OF THE
> POSSIBILITY. Nothing in these Terms waives rights that cannot be waived
> under applicable law, including under the Texas Deceptive Trade
> Practices-Consumer Protection Act.

Drafting notes for counsel: (b) is the *Italian Cowboy*-compliant clear-and-
unequivocal reliance disclaimer (Mosey pattern); the final sentence of the LoL
clause is deliberate honesty — §17.42 makes DTPA waivers void here, and
pretending otherwise invites an unconscionability framing; presentation must
be conspicuous clickwrap (affirmative click adjacent to the terms link;
acceptance logs per *Aerotek*).

### V.3 UI framing rules (build checklist for the eventual endpoint/UI)

1. §V.1 notice on every report/export screen and PDF page 1 — conspicuous per
   §1.201(b)(10); never only in Terms.
2. Status labels: `below-stated-minimum` renders **"Below stated minimum (as
   read)"** + sub-caption "the amount we read on the certificate is lower than
   the minimum stated in [citation] — verify on the certificate." `missing`
   renders "No matching document on file in CompliDrop," never "you lack a
   required license."
3. Non-verification states (`needs-document-info`, `needs-profile-info`,
   `NotCovered`, disabled rule sets) must be visually distinct from verified
   states — never a gray checkmark (*TRUSTe* principle: no implied checks).
4. Penalty text collapsed behind a "What the statute provides" affordance;
   never rendered adjacent to a red/negative badge; always sourced.
5. Every obligation shows its citation and per-item verification date.
6. No seal-like or certificate-like graphics for statuses (*SkyMed* principle).
7. Signup: affirmative click adjacent to the Terms link; log user, timestamp,
   and terms version (*Aerotek*).
8. Support macros: staff never answer "does the law require X of me?" for a
   specific customer — answer with the product's citation and "verify with the
   authority or an attorney" (*Janson*/*Cortez* line).

### V.4 Marketing claim rules

**Never:** "ensures/guarantees compliance," "keeps you compliant," "so you're
always covered," any compliance seal/badge, any claim of completeness
(*Henry Schein*; §17.46(b)(5)/(7); the substantiation doctrine).
**Safe pattern (specific, true, substantiated by the audit trail):** "checks
the documents you upload against 37 published federal and Texas requirements —
each with its citation and the date we last verified it," "tells you what's on
file and what's not," "a head start for you and your attorney — not a
substitute for either." Keep the provenance map as the standing substantiation
file. Fix the known-claim-accuracy tickets (#396–#405) before any marketing
push that references verification — trebling under §17.50(b)(1) turns on
"knowingly."

## VI. Risk assessment (with §V implemented; feature enabled post-counsel)

| Exposure | Pre-mitigation | Post-mitigation | Driver |
|---|---|---|---|
| UPL (civil injunction / criminal referral) | Moderate (Cortez-era doctrine) | **Low** | §81.101(c) safe harbor + conspicuous product notice + full automation |
| Negligent misrepresentation (subscriber fine/loss) | Moderate-high (it's an information business) | **Low-moderate** | *McCamish*/*Orca* reliance negation + economic-loss channeling + care evidence (audit trail) |
| Strict products liability | Low | **Low** | Economic-loss harm profile; *Rodgers*; book-like design cues |
| Contract claim over wrong output | Moderate | **Low** | Fee-tied cap + no-warranty (enforceable per *Bombardier*/*Arthur's Garage*) |
| DTPA private suit | Moderate | **Moderate-low** | Cannot be waived or capped — controlled only by claim accuracy + reliance element + notices |
| FTC §5 / TX AG action | Low-moderate | **Low** | Marketing rules (§V.4) + no implied verifications + substantiation file |
| Uninsured catastrophic loss | — | **Low** | Tech E&O + cyber at $1M with §IV.F placement checks |

The dominant residual risk after everything in this memo is **an affirmatively
wrong output plus marketing that overstated it** — a combination fully within
the company's control (rule-content care + claim discipline).

## VII. Open items strictly for licensed counsel

1. Confirm no post-1999 Texas authority narrows §81.101(c) for interactive/
   profile-driven software, and bless the §V.1 notice as satisfying "clearly
   and conspicuously."
2. Mark up the §V.2 rider (especially the no-reliance covenant against
   *Italian Cowboy*/*Schlumberger* line drafting standards, and the LoL
   clause's DTPA carve-out sentence).
3. Rule on the §552-vs-economic-loss channeling for a subscriber suing over
   wrong output under current Texas doctrine (the *Sharyland*/*LAN/STV* line).
4. Confirm the *Prudential/Italian Cowboy* weight of an adhesion-context
   reliance disclaimer for a $49/mo consumer-adjacent B2B product.
5. Review the status labels and penalty-text rules (§V.3 items 2–4) as applied
   to the actual UI when built.
6. Re-pull the tier-C competitor Terms quotes before relying on them in any
   filing (they are versioned pages).
7. Advise on E&O placement (application wording; fines-as-damages; the
   regulatory-action exclusion) — §IV.F.
8. Consider whether the exported PDF disclaimer (open ticket #402, existing
   product) should adopt §V.1 verbatim now, ahead of this feature.

## Appendix — authorities and verification tiers

**Tier A — verified live by the author against official/primary text
(2026-07-08/09/10):** Tex. Gov't Code §81.101(a)–(c) (+ screenshot);
Gov't Code §83.001; Tex. Bus. & Com. Code §§17.42, 17.45(4), 17.46(b)(5),(7),
17.47(a),(c), 17.49(c),(f),(g), 17.50 (treble); *Parsons* (5th Cir. — reporter
text); *Orca Assets* (holding); *Winter* (reporter text incl. chart dictum);
*Italian Cowboy* (CourtListener full text); Harbor Compliance Terms of Use
(live fetch); FTC *Henry Schein* ($250k; ensured-HIPAA claims — ftc.gov).

**Tier B — verified by two independent adversarial checkers in the research
workflow (fetched sources):** *Cortez*; *Fadia*; *American Home Assurance*
(§81.101(a) construction); *Janson*; NC consent judgment (2015 NCBC 96) +
DOJ/FTC comment letter; *Dacey*; *Brumbaugh*; §1.201(b)(10); *Dresser* (incl.
scope footnote); *Sloane*; *McCamish* (incl. the disclaimer-endorsement
passage); *D.S.A.*; *Grant Thornton*; *LAN/STV*; *Saloomey*; *Brocklesby*;
*Aetna v. Jeppesen*; *Rodgers v. Christie*; *Garcia v. Character Techs.*;
*Reyes*; *Bombardier*; *In re Online Travel*; *Barnett*; *Aerotek*; *Weitzel*
(as quoted in later authority); §17.50(a)(1)(B) reliance element; *Arthur's
Garage* (LoL enforced for negligence — **with the verified corrections**: its
conspicuousness analysis ran on the indemnity provision; the cap was reversed
as applied to DTPA misrepresentation; warranty-limitation holding extends to
EXPRESS warranties per *FDP Corp.*); *Sharyland* (with the verifier's
precision note quoted in §IV.B).

**Tier C — workflow-sourced from fetched pages, NOT independently
re-verified (verifiers lost to session limits); none is load-bearing for a
conclusion, and each is flagged inline:** *Prudential v. Jefferson Associates*
details; FTC Deception & Substantiation Policy Statement quotes; *FLEETCOR*;
*TRUSTe*; *SkyMed*; *Pizza Hut*; *Presidio*; Avalara/Mosey/Drata/Vanta/ADP/
LegalZoom Terms quotes (Gusto could not be fetched — 403 — and is excluded);
all E&O market items (Coalition/RSUI/Chubb specimen wordings, *Andy Warhol
Found.*, *Crum & Forster v. DVO*, pricing figures). Counsel should re-pull
tier-C sources before filing anything that cites them.

**Research method.** Six-domain adversarial research fan-out (each load-bearing
claim attacked by source-check and counter-authority verifiers), preceded and
followed by the author's direct reads of every keystone source. Raw claim
inventory with per-claim URLs and verification votes preserved in the session
records (workflow run `wf_af48518e-245`).
