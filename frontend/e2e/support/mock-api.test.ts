/**
 * Pins the pathMatches + waitForApi contracts added in #91 / re-exposed
 * in this module's public surface. Companion to:
 *   - `frontend/src/test/sonner.test.ts`     (#74)
 *   - `frontend/src/test/polling.test.ts`    (#82, #124)
 *   - `frontend/src/test/dropzone.test.ts`   (#84)
 *   - `frontend/src/test/security.test.ts`   (#85)
 *
 * Every other Wave-3 helper lift ships a fast-tier companion test that
 * pins the public-helper contract; #91 (`pathMatches` + `waitForApi`)
 * was the last gap. The risk profile is the same as the other helpers:
 * a regression in `pathMatches` (e.g. a "performance optimization" that
 * dropped the segment-count check and used `startsWith` instead) would
 * silently degrade every downstream E2E spec that relies on it — and
 * Playwright's slow tier wouldn't surface the regression for hours.
 *
 * Five invariants pinned for `pathMatches`:
 *   1. Exact segment-count rejection — `/api/portal` and `/api/portal/x`
 *      MUST NOT match each other in either direction.
 *   2. `:param` wildcards — `:token` matches one segment, never more.
 *   3. Literal-segment match — same-shaped templates with different
 *      literal segments don't match.
 *   4. `//` empty-segment throw — a typo'd `/api//portal` template
 *      fails LOUDLY at registration, not silently at request time.
 *   5. Pathname-only input contract — passing a full URL (scheme +
 *      host) to `actual` returns false (segment counts diverge once
 *      `https://` splits on `/`), so the no-match is correct in the
 *      protocol-violation case.
 *
 * `waitForApi`'s contract is pinned via a fake `Page` that captures
 * the predicate handed to `page.waitForResponse`. The predicate is
 * a pure function once captured, so testing it directly avoids
 * depending on a real Playwright browser context at the Vitest tier.
 */
import { describe, it, expect } from "vitest";
import type { Page } from "@playwright/test";
import { pathMatches, waitForApi } from "./mock-api";

describe("pathMatches — exact-segment-count contract (#91, #129)", () => {
  it("matches a literal path against itself", () => {
    expect(pathMatches("/api/portal", "/api/portal")).toBe(true);
  });

  it("rejects same-shape templates with different literal segments", () => {
    // Pins the literal-segment match invariant: `/api/portal` and
    // `/api/auth` are both two-segment paths, so a length-only check
    // would have passed this. The per-segment comparison must catch it.
    expect(pathMatches("/api/portal", "/api/auth")).toBe(false);
    expect(pathMatches("/api/portal/:token", "/api/auth/:token")).toBe(false);
  });

  it("rejects when the template is shorter than the actual path", () => {
    // Pins the segment-count rejection. A naive `startsWith` would
    // have passed `/api/portal` for `/api/portal/abc/info`, breaking
    // route disambiguation downstream.
    expect(pathMatches("/api/portal", "/api/portal/abc")).toBe(false);
    expect(pathMatches("/api/portal", "/api/portal/abc/info")).toBe(false);
  });

  it("rejects when the template is longer than the actual path", () => {
    // The mirror case — a `:token` template segment must NOT match
    // a missing actual segment. A future refactor that allowed
    // trailing `:param` to match a missing tail would silently
    // succeed against shorter paths.
    expect(pathMatches("/api/portal/:token", "/api/portal")).toBe(false);
    expect(pathMatches("/api/portal/:token/upload", "/api/portal/abc")).toBe(false);
  });

  it("normalizes leading and trailing slashes via filter(Boolean)", () => {
    // `/api/portal` and `/api/portal/` both split-and-filter to
    // ["api","portal"]. Pinned so a future change that switched to
    // `split("/")` WITHOUT the filter (or added a startsWith trim)
    // would surface here rather than silently breaking a smoke spec
    // that happens to read a route with a trailing slash.
    expect(pathMatches("/api/portal", "/api/portal/")).toBe(true);
    expect(pathMatches("/api/portal/", "/api/portal")).toBe(true);
  });
});

describe("pathMatches — `:param` wildcard contract (#91, #129)", () => {
  it("matches `:token` against any one segment", () => {
    expect(pathMatches("/api/portal/:token", "/api/portal/abc-123")).toBe(true);
    expect(pathMatches("/api/portal/:token", "/api/portal/9f3d2c1a7b6e5d4c")).toBe(true);
  });

  it("does NOT let `:token` span multiple segments", () => {
    // The whole reason to use a routing matcher instead of a regex —
    // `:token` is single-segment. `/api/portal/abc/info` is a two-
    // segment tail; a single `:token` must NOT swallow it.
    expect(pathMatches("/api/portal/:token", "/api/portal/abc/info")).toBe(false);
  });

  it("supports multiple `:param` segments with literal anchors", () => {
    // Pins the mixed literal+param shape used in #91's
    // `/api/portal/:token/upload` route — a regression that only
    // honored the first `:param` would surface here.
    expect(pathMatches("/api/portal/:token/upload", "/api/portal/xyz-tok/upload")).toBe(true);
    // …and the literal-segment anchor at the tail must still match.
    expect(pathMatches("/api/portal/:token/upload", "/api/portal/xyz-tok/info")).toBe(false);
  });

  it("treats any segment starting with `:` as a wildcard, regardless of name", () => {
    // The matcher keys off the leading colon, not the parameter
    // name. Two different names (`:token` vs `:id`) at the same
    // position still both match. Pinned so a future change that
    // started enforcing parameter-name agreement would surface here.
    expect(pathMatches("/api/portal/:token", "/api/portal/xyz")).toBe(true);
    expect(pathMatches("/api/portal/:id", "/api/portal/xyz")).toBe(true);
  });
});

describe("pathMatches — empty-segment throw contract (#91, #129)", () => {
  it("throws synchronously when the TEMPLATE contains `//`", () => {
    // Pins the registration-time loud-failure invariant. A typo'd
    // template like `/api//portal` would otherwise silently never
    // match anything (the filter(Boolean) drops the empty segment
    // on both sides, but the count check would still diverge against
    // any actual path with the same literal tail). Catching it at
    // call time with a NAMED error tells the test author exactly
    // where the typo lives.
    expect(() => pathMatches("/api//portal", "/api/portal")).toThrow(/empty segment/i);
    expect(() => pathMatches("/api//portal", "/api/portal")).toThrow(/likely a typo/i);
  });

  it("includes the offending template in the error message", () => {
    // The error message embeds the bad template so a future
    // contributor whose test fires this throw isn't left guessing
    // which call site produced it.
    let caught: Error | null = null;
    try {
      pathMatches("/api//portal/:token", "/api/portal/abc");
    } catch (e) {
      caught = e as Error;
    }
    expect(caught).not.toBeNull();
    expect(caught!.message).toContain("/api//portal/:token");
  });

  it("does NOT throw when the ACTUAL path contains `//`", () => {
    // The contract is "the TEMPLATE must not contain `//`"; an actual
    // path with a double slash is the request author's bug, not the
    // test author's, and gets normalized by filter(Boolean) into a
    // not-matching segment count. The matcher returns false rather
    // than throwing — pinning that asymmetry here.
    expect(() => pathMatches("/api/portal", "/api//portal")).not.toThrow();
    expect(pathMatches("/api/portal", "/api//portal")).toBe(true);
  });
});

describe("pathMatches — pathname-only input contract (#91, #129)", () => {
  it("returns false when the `actual` is a full URL with scheme+host", () => {
    // Pins the documented "must be a URL PATHNAME — no scheme, no
    // host" contract. Both current callers extract `new URL(res.url())
    // .pathname` before invoking; a future caller that forgot would
    // silently never match (segment counts diverge once `https://`
    // and the host split on `/`). Returning false here means the
    // protocol-violation surfaces as a test timeout rather than
    // matching the WRONG thing.
    expect(
      pathMatches("/api/portal/:token", "https://api.example.com/api/portal/abc"),
    ).toBe(false);
    expect(
      pathMatches("/api/portal", "http://localhost:3000/api/portal"),
    ).toBe(false);
  });
});

/**
 * `waitForApi` returns whatever `page.waitForResponse(predicate, opts)`
 * returns. The predicate is the testable surface — captured here from
 * a fake `Page` and exercised directly with synthetic `Response`-shaped
 * objects. This avoids spinning up a real Playwright browser at the
 * Vitest tier while still pinning the method+path+status filter logic
 * that downstream E2E specs rely on.
 */
type ResponsePredicate = (res: unknown) => boolean;

interface CapturedCall {
  predicate: ResponsePredicate;
  opts: { timeout?: number } | undefined;
}

function makeFakePage(): { page: Page; captured: CapturedCall[] } {
  const captured: CapturedCall[] = [];
  const fake = {
    waitForResponse: (
      predicate: ResponsePredicate,
      opts: { timeout?: number } | undefined,
    ) => {
      captured.push({ predicate, opts });
      // The real return type is `Promise<Response>`; tests in this
      // file never await it, so an empty promise is sufficient.
      return Promise.resolve({}) as unknown as ReturnType<Page["waitForResponse"]>;
    },
  };
  return { page: fake as unknown as Page, captured };
}

function fakeResponse(method: string, url: string, status = 200) {
  return {
    request: () => ({ method: () => method }),
    url: () => url,
    status: () => status,
  };
}

describe("waitForApi — predicate contract (#91, #129)", () => {
  it("matches when method and path agree", () => {
    const { page, captured } = makeFakePage();
    waitForApi(page, "POST", "/api/portal/:token/upload");
    expect(captured).toHaveLength(1);
    const { predicate } = captured[0];
    expect(predicate(fakeResponse("POST", "http://x/api/portal/abc/upload"))).toBe(true);
  });

  it("rejects when the method differs", () => {
    // The downstream `await response` would silently match the WRONG
    // request if waitForApi accepted any method. Pinning case-
    // insensitive method comparison (the helper uppercases on both
    // sides) here.
    const { page, captured } = makeFakePage();
    waitForApi(page, "POST", "/api/portal/:token/upload");
    const { predicate } = captured[0];
    expect(predicate(fakeResponse("GET", "http://x/api/portal/abc/upload"))).toBe(false);
    expect(predicate(fakeResponse("DELETE", "http://x/api/portal/abc/upload"))).toBe(false);
    // Case-insensitive: "post" still matches "POST".
    expect(predicate(fakeResponse("post", "http://x/api/portal/abc/upload"))).toBe(true);
  });

  it("rejects when the path differs (delegating to pathMatches)", () => {
    // The whole reason `pathMatches` is exported — waitForApi MUST
    // use the same matcher the mockApi route table uses, otherwise
    // the route declaration and its wait disagree on what `:token`
    // captures.
    const { page, captured } = makeFakePage();
    waitForApi(page, "POST", "/api/portal/:token/upload");
    const { predicate } = captured[0];
    // Same segment count but different literal anchor — should miss.
    expect(predicate(fakeResponse("POST", "http://x/api/portal/abc/info"))).toBe(false);
    // Too many segments — should miss.
    expect(predicate(fakeResponse("POST", "http://x/api/portal/abc/upload/extra"))).toBe(false);
  });

  it("filters by exact status when `status` is provided", () => {
    // A future caller that wanted to assert the 429-throttle path
    // would pass `status: 429`. A different status (200, 500) on
    // the same URL must NOT satisfy the predicate.
    const { page, captured } = makeFakePage();
    waitForApi(page, "GET", "/api/portal/:token", { status: 429 });
    const { predicate, opts } = captured[0];
    expect(opts).toMatchObject({ timeout: 15_000 });
    expect(predicate(fakeResponse("GET", "http://x/api/portal/abc", 429))).toBe(true);
    expect(predicate(fakeResponse("GET", "http://x/api/portal/abc", 200))).toBe(false);
    expect(predicate(fakeResponse("GET", "http://x/api/portal/abc", 500))).toBe(false);
  });

  it("lets any status through when `status` is omitted", () => {
    // The documented default: "any HTTP status". A regression that
    // started defaulting to 200-only would silently break every
    // existing call site that doesn't pass `status`.
    const { page, captured } = makeFakePage();
    waitForApi(page, "GET", "/api/portal/:token");
    const { predicate } = captured[0];
    expect(predicate(fakeResponse("GET", "http://x/api/portal/abc", 200))).toBe(true);
    expect(predicate(fakeResponse("GET", "http://x/api/portal/abc", 429))).toBe(true);
    expect(predicate(fakeResponse("GET", "http://x/api/portal/abc", 500))).toBe(true);
  });

  it("defaults `timeout` to 15_000 ms and honors a custom value", () => {
    // The documented default matches the project's existing inline
    // `waitForResponse` calls; a custom timeout passes through
    // unchanged.
    const { page, captured } = makeFakePage();
    waitForApi(page, "GET", "/api/portal/:token");
    expect(captured[0].opts).toMatchObject({ timeout: 15_000 });

    const { page: page2, captured: captured2 } = makeFakePage();
    waitForApi(page2, "GET", "/api/portal/:token", { timeout: 1_500 });
    expect(captured2[0].opts).toMatchObject({ timeout: 1_500 });
  });
});
