/**
 * Pins the expectTokenNotInHead contract added in #127. Mirrors the
 * companion test pattern established by #74 / #82 / #84 / #85 / #91:
 * every helper that ships in `e2e/support/` carries a fast-tier
 * contract test pinning its public surface.
 *
 * Tests use a fake Playwright `Page` (the same shape as the
 * `mock-api.test.ts` fake-page tier) — capturing the `innerHTML()`
 * call against a stubbed locator avoids depending on a real
 * Playwright browser context. The helper is pure once the locator's
 * innerHTML resolves; testing it directly at the Vitest tier
 * catches regressions before Playwright's slow tier does.
 *
 * Invariants pinned:
 *   1. Passes silently when the token is absent.
 *   2. Catches a literal-value leak (token rendered as a meta-tag
 *      attribute value).
 *   3. Catches an HTML-entity-escaped leak (token containing `&`
 *      that round-trips through attribute serialization as `&amp;`).
 *   4. Error message uses `summarize()` shape — length + 4-char
 *      prefix/suffix for long values, length-only for ≤ 8 chars.
 *      NEVER discloses the full token to CI logs.
 */
import { describe, it, expect } from "vitest";
import type { Page } from "@playwright/test";
import { expectTokenNotInHead } from "./security";

function makeFakePage(headHtml: string): Page {
  const fake = {
    locator: (selector: string) => {
      if (selector !== "head") {
        throw new Error(
          `expectTokenNotInHead must only call page.locator("head") — got ${selector}`,
        );
      }
      return {
        innerHTML: () => Promise.resolve(headHtml),
      };
    },
  };
  return fake as unknown as Page;
}

describe("expectTokenNotInHead — input validation (#127)", () => {
  it("throws loudly when the token is the empty string", async () => {
    // Foot-gun: `process.env.SOME_TOKEN ?? ""` produces "" when the
    // env var is unset. `"".includes("")` is always true, so without
    // a guard the helper would unconditionally throw "leak detected"
    // with a length-0 sentinel — confusing the test author. Pin the
    // explicit guard so a regression that removed it would surface
    // here rather than as misleading failures in downstream specs.
    const page = makeFakePage("<title>safe</title>");
    await expect(expectTokenNotInHead(page, "")).rejects.toThrow(
      /token must be non-empty/,
    );
  });
});

describe("expectTokenNotInHead — basic contract (#127)", () => {
  it("passes silently when the token is absent from <head>", async () => {
    const page = makeFakePage(
      "<title>CompliDrop</title><meta name='viewport' content='width=device-width'>",
    );
    await expect(
      expectTokenNotInHead(page, "secret-portal-token-not-present"),
    ).resolves.toBeUndefined();
  });

  it("catches a literal-value leak in a <meta> attribute", async () => {
    // The documented dominant head-injection vector: a vendor-token
    // meta tag whose `content="…"` attribute carries the value. A
    // regression that added `<meta name="vendor-token" content={token}>`
    // (e.g. via a misguided "ship the token for the SPA to read" pass)
    // would surface here.
    const token = "leaked-portal-token-1234567890";
    const page = makeFakePage(
      `<meta name="vendor-token" content="${token}"><title>CompliDrop</title>`,
    );
    await expect(expectTokenNotInHead(page, token)).rejects.toThrow(
      /appeared in <head>\.innerHTML/,
    );
  });

  it("catches an HTML-entity-escaped leak (`&` in token round-trips as `&amp;`)", async () => {
    // The same regression class the component helper's
    // `assertNotInDom` escaped-form scan exists to catch. A token
    // containing `&` would be serialized into the attribute as
    // `&amp;`; a literal `.includes(token)` would miss it. Pinning
    // the two-form scan stays consistent across both tiers.
    const token = "r&d-token-leaked-via-attr";
    const page = makeFakePage(
      `<meta name="x" content="r&amp;d-token-leaked-via-attr"><title>x</title>`,
    );
    await expect(expectTokenNotInHead(page, token)).rejects.toThrow(
      /HTML-entity-escaped form/,
    );
  });

  it("catches a leak in <title> text content", async () => {
    // A regression that injected the token into <title> via
    // next/head metadata (e.g. `title: \`Portal: ${token}\``) would
    // contribute to innerHTML through the title's child text. The
    // scan covers it without additional configuration.
    const token = "secret-token-in-title-12345";
    const page = makeFakePage(
      `<title>Portal: ${token}</title><meta name="viewport" content="x">`,
    );
    await expect(expectTokenNotInHead(page, token)).rejects.toThrow(
      /appeared in <head>\.innerHTML/,
    );
  });

  it("catches a leak in inline <script> body in <head>", async () => {
    // A regression that bootstrapped the token via an inline
    // <script>const PORTAL_TOKEN = "…"</script> in <head> (e.g. a
    // misguided "SSR the token into a global for the SPA" change)
    // would also be in innerHTML.
    const token = "bootstrapped-portal-token-9876";
    const page = makeFakePage(
      `<script>const PORTAL_TOKEN = "${token}"</script>`,
    );
    await expect(expectTokenNotInHead(page, token)).rejects.toThrow(
      /appeared in <head>\.innerHTML/,
    );
  });
});

describe("expectTokenNotInHead — error-message sanitization (#127)", () => {
  it("does NOT echo the full sensitive value into the error message", async () => {
    // The whole point of the sanitization — a real security
    // regression must not be the same path that discloses the token
    // to CI logs. Pin the no-full-disclosure invariant directly so
    // a future refactor that "improved" diagnostics by interpolating
    // the raw token fails here.
    const realisticToken = "vendor-portal-token-9f3d2c1a7b6e5d4c";
    const page = makeFakePage(
      `<meta name="vendor-token" content="${realisticToken}">`,
    );
    let caught: Error | null = null;
    try {
      await expectTokenNotInHead(page, realisticToken);
    } catch (e) {
      caught = e as Error;
    }
    expect(caught).not.toBeNull();
    expect(caught!.message).not.toContain(realisticToken);
    // Length + prefix + suffix sentinel SHOULD appear.
    expect(caught!.message).toMatch(/length 36/);
    expect(caught!.message).toContain('"vend"');
    expect(caught!.message).toContain('"5d4c"');
  });

  it("short values (≤ 8 chars) report length only — no near-full prefix+suffix disclosure", async () => {
    // Mirror of the component-tier security.test.ts test — a 6-char
    // value like "abc123" would have a 4-char prefix overlapping
    // with the 4-char suffix; together they'd effectively disclose
    // the entire value. Length-only for ≤ 8 chars.
    const shortToken = "abc123";
    const page = makeFakePage(`<title>${shortToken}</title>`);
    let caught: Error | null = null;
    try {
      await expectTokenNotInHead(page, shortToken);
    } catch (e) {
      caught = e as Error;
    }
    expect(caught).not.toBeNull();
    expect(caught!.message).not.toContain(shortToken);
    expect(caught!.message).toMatch(/length 6/);
  });
});
