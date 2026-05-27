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
import { KNOWN_PLAN_IDS, parsePlanId, PLANS } from "./plans";

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

describe("PLANS — id ↔ entry orphan check (#71 followup)", () => {
  it("contains exactly one entry per KNOWN_PLAN_IDS — no orphans, no extras", () => {
    // The TypeScript Record<PlanId, ...> already enforces this at
    // compile time. The runtime assertion guards against any future
    // `as any` cast that bypasses the type — the entire module's
    // contract relies on every id resolving to a valid entry.
    const planIdsInPlans = Object.keys(PLANS).sort();
    const knownIds = [...KNOWN_PLAN_IDS].sort();
    expect(planIdsInPlans).toEqual(knownIds);
  });

  it("each entry's id field matches its key", () => {
    // A copy-paste bug where `PLANS.annual.id === "pro"` would route
    // every annual user through pro pricing. Pin the self-consistency.
    for (const id of KNOWN_PLAN_IDS) {
      expect(PLANS[id].id).toBe(id);
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
});
