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

  it("treats the root path `/` as a zero-segment match against itself, and rejects any deeper actual", () => {
    // Edge case: both template and actual split-and-filter to []
    // when they're just "/". Length check passes trivially (0===0)
    // and the loop body never runs, so the function returns true.
    // Pinned so a future 'optimization' that early-returned false
    // when `tParts.length === 0` (mistaking it for a degenerate
    // input) wouldn't silently break a mock that registered a root
    // route. Symmetric: any deeper actual must still mismatch on
    // segment count.
    expect(pathMatches("/", "/")).toBe(true);
    expect(pathMatches("/", "")).toBe(true);
    expect(pathMatches("", "/")).toBe(true);
    expect(pathMatches("/", "/api")).toBe(false);
    expect(pathMatches("/", "/api/portal")).toBe(false);
  });
});

describe("pathMatches — `:param` wildcard contract (#91, #129)", () => {
  it("matches `:token` against any one segment", () => {
    expect(pathMatches("/api/portal/:token", "/api/portal/abc-123")).toBe(true);
    // Token-shaped (16-char hex) variant deliberately uses a NON-
    // portal path: `frontend/e2e/scripts/scan-secrets.mjs` flags any
    // `/api/portal/[A-Za-z0-9_-]{16,}` substring as a leaked vendor
    // portal token. Empirically (run #167 against commit 7ccb6c3) the
    // CI E2E artifact directory captures this fixture's source even
    // though `playwright.config.ts`'s `testMatch: /.*\.spec\.ts$/`
    // filters `.test.ts` files out of the test run — the exact
    // mechanism wasn't reproducible locally with `CI=1 npm run
    // test:e2e`, so the safest defense is to keep token-shaped
    // fixtures off the canonical portal path. The scan stays useful
    // against real artifacts that way.
    expect(pathMatches("/api/users/:id", "/api/users/9f3d2c1a7b6e5d4c")).toBe(true);
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

  it("treats a bare `:` and repeated wildcard names as valid wildcards", () => {
    // The matcher's "is wildcard" check is `startsWith(':')`, which
    // accepts a single-char `:` segment AND duplicate parameter
    // names (`:x` / `:x` in the same template). Pinning the loose
    // grammar so a future contributor who tightened the rule (e.g.
    // started requiring `/^:[a-z][a-z0-9]*$/`) would have to update
    // this test deliberately rather than silently breaking a caller
    // that relied on the loose shape.
    expect(pathMatches("/api/:", "/api/anything")).toBe(true);
    expect(pathMatches("/api/:x/:x", "/api/foo/bar")).toBe(true);
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

  it("returns false when `actual` ends in a query string or hash fragment on a literal segment", () => {
    // The JSDoc on `pathMatches` enumerates "no query string, no hash
    // fragment" as part of the pathname-only contract; this test pins
    // the half of the contract the matcher NATURALLY enforces — when
    // the trailing segment is LITERAL, segment-equality rejects a
    // `?…` / `#…` suffix because `"portal" !== "portal?foo=bar"`. A
    // naive 'be lenient' refactor that pre-stripped `?` from `actual`
    // would silently start matching the wrong literal route under
    // `waitForApi` whenever a future caller forgot the
    // `new URL(res.url()).pathname` extraction — this test would
    // surface that regression.
    //
    // The OTHER half of the contract — `:param`-terminal segments
    // would let `abc?retry=1` through because the wildcard accepts
    // any non-empty single segment — is held by callers (both
    // current call sites extract `.pathname`), not by the matcher.
    // No defensive check here, intentionally: this matcher exists
    // to support two test files, and adding prophylactic validation
    // would expand scope past #129. Pinning the literal-terminal
    // case is the half the implementation owns; the `:param` case
    // stays a call-site contract documented in the JSDoc.
    expect(pathMatches("/api/portal", "/api/portal?foo=bar")).toBe(false);
    expect(pathMatches("/api/portal", "/api/portal#section")).toBe(false);
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
      // Eagerly invoke the predicate with a benign stub so any
      // construction-time throw inside `waitForApi` (e.g. if a
      // future refactor moved validation into the predicate
      // factory) surfaces as a synchronous test failure rather
      // than a captured-but-never-exercised state.
      predicate(fakeResponse("GET", "http://x/"));
      captured.push({ predicate, opts });
      // The real return type is `Promise<Response>`; tests in this
      // file never await it. The resolved (never-rejecting) promise
      // returned here keeps Node's unhandled-rejection surface
      // quiet today, and the eager `predicate(...)` invocation
      // above turns any future construction-time throw into a
      // synchronous failure that surfaces immediately rather than
      // through a floating rejection.
      return Promise.resolve({}) as unknown as ReturnType<
        Page["waitForResponse"]
      >;
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
    // the same URL must NOT satisfy the predicate. (Timeout default
    // is exercised by the dedicated test below — kept out of here
    // to keep one invariant per test.)
    const { page, captured } = makeFakePage();
    waitForApi(page, "GET", "/api/portal/:token", { status: 429 });
    const { predicate } = captured[0];
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
