import { describe, it, expect, vi, afterEach } from "vitest";
import {
  absoluteUrl,
  SITE_URL,
  SITE_NAME,
  PRO_PRICE_USD,
  SITE_DESCRIPTION,
  SITE_DESCRIPTION_MAX_CHARS,
} from "./site";

describe("site facts", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.resetModules();
  });

  it("composes absolute URLs without double slashes", () => {
    expect(absoluteUrl("/")).toBe(`${SITE_URL}/`);
    expect(absoluteUrl("/glossary/certificate-of-insurance")).toBe(
      `${SITE_URL}/glossary/certificate-of-insurance`,
    );
    expect(absoluteUrl("/faq")).toMatch(/^https?:\/\//);
  });

  it("derives the Pro price as a bare numeric string (feeds the schema Offer)", () => {
    expect(PRO_PRICE_USD).toMatch(/^[0-9.]+$/);
  });

  it("exposes the canonical brand name", () => {
    expect(SITE_NAME).toBe("CompliDrop");
  });

  it("keeps SITE_DESCRIPTION inside the bound its own doc comment states (#403)", () => {
    // The comment promised "≤ ~160 chars so it works verbatim as a
    // <meta name=description>" while the value was 218 — and this string is
    // ALSO the manifest description and both JSON-LD entities' description, so
    // nothing else would ever have noticed. A file must not ship a constraint
    // it violates: the bound is now enforced, not merely documented.
    //
    // The bound itself is pinned first. Without this the test only enforces
    // "value ≤ whatever this file currently declares", and raising the constant
    // is the natural one-line edit someone makes when the copy grows — which
    // would keep the suite green while the search-engine fact the comment cites
    // (~160 characters) quietly stopped being enforced.
    expect(SITE_DESCRIPTION_MAX_CHARS).toBe(160);
    expect(SITE_DESCRIPTION.length).toBeLessThanOrEqual(SITE_DESCRIPTION_MAX_CHARS);
    // Anti-vacuous: an empty or stub description would satisfy the bound.
    expect(SITE_DESCRIPTION.length).toBeGreaterThan(80);
    expect(SITE_DESCRIPTION).toMatch(/COI tracking software/i);
  });

  it("falls back to the production origin when NEXT_PUBLIC_SITE_URL is empty", async () => {
    // The documented guard: CI/preview envs often forward the var without a
    // value, and `new URL("")` throws — so an empty string must be treated as
    // unset, not used verbatim.
    vi.stubEnv("NEXT_PUBLIC_SITE_URL", "");
    vi.resetModules();
    const fresh = await import("./site");
    expect(fresh.SITE_URL).toMatch(/^https:\/\//);
    expect(() => fresh.absoluteUrl("/")).not.toThrow();
  });
});
