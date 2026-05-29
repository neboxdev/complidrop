import { describe, it, expect, vi, afterEach } from "vitest";
import { absoluteUrl, SITE_URL, SITE_NAME, PRO_PRICE_USD } from "./site";

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
