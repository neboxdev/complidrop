/**
 * Pins the contract of the plan-pricing single-source-of-truth module
 * added in #71 (with the price-bearing cross-consistency invariants
 * added in the #71 followup review).
 *
 * Mirrors the Wave 3+ pattern (sonner / polling / dropzone / security
 * / form-helpers): every helper module ships with a companion test
 * pinning its contract so a future "simplification" can't silently
 * regress the invariants downstream callers depend on.
 *
 * Four invariants matter for downstream UI:
 *   1. `parsePlanId` produces stable output for every input shape
 *      callers pass (null, undefined, empty, mixed-case, whitespace,
 *      unknown). The register page reads `searchParams.get("plan")`
 *      which can be any of these.
 *   2. Every id in `KNOWN_PLAN_IDS` has a `PLANS[id]` entry. The
 *      TypeScript `Record<PlanId, ...>` enforces this at compile time
 *      already; this test pins it at runtime too so a hypothetical
 *      `as any` cast escape can't sneak past type-check.
 *   3. Price-drift detector: each `PLANS[id].monthlyPriceLabel` equals
 *      a literal that's expected to appear in marketing-side rendered
 *      output. A regression that changed `$49` to `$0` silently would
 *      otherwise pass type-check and lint.
 *   4. Cross-consistency inside `PLANS` itself: each plan's
 *      `bannerCopy` (when non-null) contains its own
 *      `monthlyPriceLabel`. A future price change that touched the
 *      label but forgot the banner produced an internally
 *      contradictory plan (banner: "$39/month, billed $468/year" but
 *      headline label: "$35") — this test makes that contradiction
 *      a build break.
 */
import { describe, it, expect } from "vitest";
import {
  KNOWN_CHECKOUT_PLAN_IDS,
  KNOWN_PLAN_IDS,
  parsePlanId,
  PLANS,
} from "./plans";

describe("parsePlanId — tolerant URL-param parser (#71)", () => {
  it("returns 'free' for null", () => {
    expect(parsePlanId(null)).toBe("free");
  });

  it("returns 'free' for undefined", () => {
    expect(parsePlanId(undefined)).toBe("free");
  });

  it("returns 'free' for empty string", () => {
    expect(parsePlanId("")).toBe("free");
  });

  it("returns the plan id for an exact-match input", () => {
    expect(parsePlanId("free")).toBe("free");
    expect(parsePlanId("pro")).toBe("pro");
    expect(parsePlanId("annual")).toBe("annual");
  });

  it("lowercases mixed-case input before the allow-list check (#71 followup)", () => {
    // Marketing emails + copy/pasted links arrive with mixed case
    // (?plan=Annual). The tolerant parse keeps the user's choice.
    expect(parsePlanId("Annual")).toBe("annual");
    expect(parsePlanId("ANNUAL")).toBe("annual");
    expect(parsePlanId("Pro")).toBe("pro");
    expect(parsePlanId("PRO")).toBe("pro");
  });

  it("trims surrounding whitespace before the allow-list check (#71 followup)", () => {
    expect(parsePlanId(" annual ")).toBe("annual");
    expect(parsePlanId("\tpro\n")).toBe("pro");
    expect(parsePlanId("  free  ")).toBe("free");
  });

  it("falls back to 'free' for an unknown plan id (allow-list is the boundary)", () => {
    expect(parsePlanId("enterprise")).toBe("free");
    expect(parsePlanId("teams")).toBe("free");
    // Attack-shaped values that would otherwise propagate into PLANS[plan] —
    // the allow-list catches them.
    expect(parsePlanId("__proto__")).toBe("free");
    expect(parsePlanId("constructor")).toBe("free");
    expect(parsePlanId("<script>alert(1)</script>")).toBe("free");
  });
});

describe("KNOWN_PLAN_IDS — the canonical plan-id list (#71)", () => {
  it("contains exactly the three documented ids in stable order", () => {
    // Stable order matters: page.test.tsx and other consumers iterate
    // this array to assert every plan has a corresponding CTA. A
    // future reorder would still satisfy the includes() check but
    // could affect rendered nav/card order if a caller depends on
    // position (none do today — pin so it stays that way).
    expect(KNOWN_PLAN_IDS).toEqual(["free", "pro", "annual"]);
  });

  it("has length 3 (catches an accidental drop)", () => {
    expect(KNOWN_PLAN_IDS).toHaveLength(3);
  });
});

describe("PLANS — id ↔ entry orphan check (#71 followup + #147)", () => {
  it("contains exactly one entry per id in (KNOWN_PLAN_IDS ∪ KNOWN_CHECKOUT_PLAN_IDS) — no orphans, no extras", () => {
    // The TypeScript Record<PlanId | CheckoutPlanId, ...> already
    // enforces this at compile time. The runtime assertion guards
    // against any future `as any` cast that bypasses the type — the
    // entire module's contract relies on every id resolving to a
    // valid entry.
    //
    // Post-#147 (ADR 0011): the union covers free / pro / annual
    // (KNOWN_PLAN_IDS — public URL-reachable) and founding
    // (KNOWN_CHECKOUT_PLAN_IDS — authenticated-only promo).
    const planIdsInPlans = Object.keys(PLANS).sort();
    const expectedIds = [
      ...new Set([...KNOWN_PLAN_IDS, ...KNOWN_CHECKOUT_PLAN_IDS]),
    ].sort();
    expect(planIdsInPlans).toEqual(expectedIds);
  });

  it("each entry's id field matches its key (KNOWN_PLAN_IDS)", () => {
    // A copy-paste bug where `PLANS.annual.id === "pro"` would route
    // every annual user through pro pricing. Pin the self-consistency.
    for (const id of KNOWN_PLAN_IDS) {
      expect(PLANS[id].id).toBe(id);
    }
  });

  it("each entry's id field matches its key (KNOWN_CHECKOUT_PLAN_IDS — covers founding)", () => {
    // founding isn't in KNOWN_PLAN_IDS, so the prior loop wouldn't
    // touch it. This loop adds coverage for the checkout-only branch.
    for (const id of KNOWN_CHECKOUT_PLAN_IDS) {
      expect(PLANS[id].id).toBe(id);
    }
  });
});

describe("KNOWN_CHECKOUT_PLAN_IDS — the canonical checkout-eligible plan-id list (#147, ADR 0011)", () => {
  it("contains exactly the three documented checkout-eligible ids in stable order", () => {
    // Stable order matters: settings/page.tsx iterates this array to
    // render the billing tiles. A reorder would still satisfy the
    // includes() check but would change tile order on the page. Pin
    // so a future "I just reordered the alphabet" PR can't silently
    // change the conversion-default highlight (the middle tile,
    // `annual`, is the featured one).
    expect(KNOWN_CHECKOUT_PLAN_IDS).toEqual(["pro", "annual", "founding"]);
  });

  it("has length 3 (catches an accidental drop)", () => {
    expect(KNOWN_CHECKOUT_PLAN_IDS).toHaveLength(3);
  });

  it("excludes 'free' (no checkout flow for free signup)", () => {
    // Free is reached via `?plan=free` URL → register, not via the
    // settings billing tiles. KNOWN_PLAN_IDS includes it (URL-
    // reachable); KNOWN_CHECKOUT_PLAN_IDS does not.
    expect(KNOWN_CHECKOUT_PLAN_IDS).not.toContain("free");
  });

  it("includes 'founding' (authenticated-only promo per ADR 0011)", () => {
    // Founding is the asymmetric case: in KNOWN_CHECKOUT_PLAN_IDS
    // (the settings tile + wire vocab) but NOT in KNOWN_PLAN_IDS
    // (no public landing-page CTA, no ?plan=founding URL). If a
    // future contributor "cleans up" by removing founding from
    // KNOWN_CHECKOUT_PLAN_IDS without also removing the Stripe
    // FoundingPriceId + the backend switch case, the test catches
    // the inconsistency.
    expect(KNOWN_CHECKOUT_PLAN_IDS).toContain("founding");
  });

  it("KNOWN_PLAN_IDS excludes 'founding' (founding stays auth-only per ADR 0011)", () => {
    // The mirror invariant of the previous test. If a future PR adds
    // founding to KNOWN_PLAN_IDS to make it a public CTA, that's the
    // marketing pivot described in ADR 0011 alternative B and
    // requires its own spec — the test failing here is the signal
    // to load ADR 0011 and revisit the decision.
    expect(KNOWN_PLAN_IDS as readonly string[]).not.toContain("founding");
  });

  it("KNOWN_CHECKOUT_PLAN_IDS ⊂ Object.keys(PLANS) — every checkout id has a registry entry", () => {
    // Sanity: every checkout plan id resolves to a PLANS entry so
    // the settings page's `PLANS[id].monthlyPriceLabel` lookup
    // never returns undefined.
    for (const id of KNOWN_CHECKOUT_PLAN_IDS) {
      expect(PLANS[id]).toBeDefined();
      expect(PLANS[id].monthlyPriceLabel).toMatch(/^\$\d/);
    }
  });
});

describe("PLANS — price-drift detection (#71 + #71 followup)", () => {
  // These literal assertions ARE load-bearing: they fail if someone
  // changes a price in the module without intending to. The test must
  // be updated INTENTIONALLY alongside any deliberate price change.
  it("Pro is $49/month", () => {
    expect(PLANS.pro.monthlyPriceLabel).toBe("$49");
  });

  it("Annual is $39/month", () => {
    expect(PLANS.annual.monthlyPriceLabel).toBe("$39");
  });

  it("Annual billed total is $468/year", () => {
    expect(PLANS.annual.annualBilledLabel).toContain("$468");
  });

  it("Annual savings is $120", () => {
    expect(PLANS.annual.annualSavingsLabel).toContain("$120");
  });

  it("Free is $0", () => {
    expect(PLANS.free.monthlyPriceLabel).toBe("$0");
  });

  it("Founding is $39/month (#147)", () => {
    // Founding is the checkout-only promo tier introduced into PLANS
    // by #147 + ADR 0011 (previously hardcoded `$39` in
    // settings/page.tsx). A regression that dropped the price or
    // diverged from Annual's $39 would surface here.
    expect(PLANS.founding.monthlyPriceLabel).toBe("$39");
  });
});

describe("PLANS — cross-consistency invariants (#71 followup)", () => {
  it("Pro's bannerCopy contains its own monthlyPriceLabel — banner can't drift from headline", () => {
    // If a future contributor changes monthlyPriceLabel to "$59" but
    // forgets to update bannerCopy, the test fails — exactly the
    // silent-drift the single-source-of-truth contract exists to
    // prevent. The whole point of the module.
    expect(PLANS.pro.bannerCopy).toContain(PLANS.pro.monthlyPriceLabel);
  });

  it("Annual's bannerCopy contains its monthlyPriceLabel", () => {
    expect(PLANS.annual.bannerCopy).toContain(PLANS.annual.monthlyPriceLabel);
  });

  it("Annual's bannerCopy contains the billed-total figure ($468)", () => {
    // The annual upsell hinges on showing the yearly billed total
    // ($468) in the banner — a regression that dropped it would
    // weaken the conversion message without anyone noticing.
    expect(PLANS.annual.bannerCopy).toContain("$468");
  });

  it("Annual's bannerCopy contains the savings figure ($120)", () => {
    expect(PLANS.annual.bannerCopy).toContain("$120");
  });

  it("Free has null bannerCopy (no upsell on free signup)", () => {
    // Pre-#71 the register page rendered the banner conditionally on
    // PLAN_COPY[plan].banner being non-null. Pin that contract here
    // so a future entry that accidentally added a free-tier banner
    // would surface as a test failure.
    expect(PLANS.free.bannerCopy).toBeNull();
  });

  it("annualSavingsLabel is canonically lowercase (#71 followup)", () => {
    // The label is consumed mid-sentence on the landing page
    // ("Billed $468/year — save $120"). Storing canonical lowercase
    // means no per-call-site `.toLowerCase()` transform is needed,
    // and the file's docstring matches the rendered output. Pin so
    // a future "capitalize for consistency" edit doesn't silently
    // change the landing-page render.
    expect(PLANS.annual.annualSavingsLabel).toBe("save $120");
  });

  it("Founding has null bannerCopy (auth-only promo per ADR 0011)", () => {
    // ADR 0011: Founding is surfaced only after authentication (the
    // settings billing tiles), never via `?plan=founding` URLs. The
    // register-form banner is keyed by `KNOWN_PLAN_IDS` which
    // excludes founding, so this field would never render — keep
    // it null so a future "add a Founding banner" PR has to first
    // promote founding into KNOWN_PLAN_IDS (the marketing pivot).
    expect(PLANS.founding.bannerCopy).toBeNull();
  });

  it("Founding has a tagline for the settings billing tile (#147)", () => {
    // The settings page renders `PLANS[id].tagline ?? ""` for each
    // checkout-eligible plan's billing tile. Pin that founding has
    // a non-empty tagline — `null` would render an empty `<p>` and
    // visually break the tile layout.
    expect(PLANS.founding.tagline).toBeTruthy();
    expect(PLANS.founding.tagline).toContain("First 50");
  });

  it("Each KNOWN_CHECKOUT_PLAN_IDS entry has a non-null tagline (settings tile renders all three)", () => {
    // The settings billing tiles iterate KNOWN_CHECKOUT_PLAN_IDS and
    // render each tagline. A null tagline on any of them would show
    // an empty `<p>` block; force every checkout tier to ship its
    // own one-line marketing copy.
    for (const id of KNOWN_CHECKOUT_PLAN_IDS) {
      expect(PLANS[id].tagline).toBeTruthy();
    }
  });
});
