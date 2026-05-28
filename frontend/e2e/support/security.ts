/**
 * E2E-tier security assertions (#127).
 *
 * Companion to the component-tier `assertNotInDom` helper at
 * [`frontend/src/test/security.ts`](../../src/test/security.ts). That
 * helper scans `document.body` only and explicitly defers
 * head-injection coverage to the E2E layer. This module is that
 * layer: a Playwright-flavored helper for asserting a sensitive value
 * (vendor portal token, session token, etc.) is absent from the
 * rendered `<head>` of the current page.
 *
 * Why a separate file rather than inline-in-the-spec: the harness-
 * lift pattern (sonner #74, polling #82, dropzone #84, security #85,
 * form-helpers #91) exists so future tests reach for the helper by
 * import, not by copy-paste. If a future flow spec (auth, login,
 * dashboard) needs to assert a session/refresh token isn't in the
 * head, importing from here is the right shape — re-implementing
 * inline would drift in error-message sanitization, scan coverage,
 * or escape handling. The companion test at
 * [`security.test.ts`](./security.test.ts) pins the contract at the
 * fast Vitest tier so a regression surfaces before Playwright runs.
 *
 * ## What this helper covers
 *
 * - `<meta>` attribute values (the documented dominant head-
 *   injection vector — e.g. `<meta name="vendor-token"
 *   content="…">`).
 * - `<title>` text content (rendered as a child of `<head>`).
 * - `<script>` body inline in `<head>` (e.g. `<script>const TOK =
 *   "…"</script>`).
 * - HTML-entity-escaped forms of the value (mirror of
 *   `assertNotInDom`'s two-form scan) so a token containing `&` /
 *   `<` / `>` / `"` is caught even after attribute serialization
 *   rewrites it.
 *
 * ## What this helper does NOT cover
 *
 * - `document.body` — covered by `assertNotInDom`.
 * - `localStorage` / `sessionStorage` / `window.*` — non-DOM
 *   channels, explicitly out of scope (matches the body helper's
 *   scope limits).
 * - Shadow DOM — none today; add a `scanShadow` opt if a future
 *   component uses shadow roots.
 *
 * ## Error-message sanitization
 *
 * On failure the helper reports the value's LENGTH plus (for values
 * > 8 chars) a 4-char prefix + 4-char suffix sentinel — NOT the full
 * value. Mirrors the `summarize()` shape from the component-tier
 * helper so a regression on a production-shaped token never
 * discloses the token to CI logs. For values ≤ 8 chars the prefix +
 * suffix would be near-full disclosure, so length-only is reported.
 */
import type { Page } from "@playwright/test";

/**
 * Asserts that `token` does NOT appear in `page.locator('head')
 * .innerHTML()` — neither in literal form nor in HTML-entity-escaped
 * form. Throws a plain Error on failure with a sanitized message
 * (no full-token disclosure).
 *
 * Arm AFTER the page has settled into the state under test — call
 * `await waitForApi(...)` / `await expect(...).toBeVisible(...)`
 * first so the head reflects the final render, not an in-flight one.
 *
 * IMPORTANT: this helper deliberately uses `throw new Error(...)`
 * rather than Playwright's `expect(headHtml).not.toContain(token)`.
 * Playwright's `expect` chain auto-includes a `Received string: …`
 * field in the failure output whose contents would dump the raw
 * `<head>` HTML — defeating the sanitization point and disclosing
 * the leaked token to CI logs. Throwing a plain Error with a
 * pre-summarized message keeps full control over the disclosure
 * surface. Mirrors the same pattern in
 * [`assertNotInDom`](../../src/test/security.ts).
 */
export async function expectTokenNotInHead(
  page: Page,
  token: string,
): Promise<void> {
  const headHtml = await page.locator("head").innerHTML();
  const sentinel = summarize(token);
  const escaped = escapeHtml(token);

  // Two checks — literal substring AND HTML-entity-escaped substring.
  // The escaped form catches the leak class where a token contains
  // `&` / `<` / `>` / `"` and gets rewritten during attribute
  // serialization (e.g. `r&d-token` → `r&amp;d-token`). Production
  // portal tokens are URL-safe base64 (no specials) so for them the
  // escaped form equals the literal — the second check is a no-op
  // and the message remains accurate. The check stays for future
  // callers with differently-shaped sensitive values.
  if (headHtml.includes(token)) {
    throw new Error(
      `head-injection token-absence assertion failed: sensitive value ${sentinel} ` +
        `appeared in <head>.innerHTML — production code is leaking a sensitive ` +
        `value through a <meta>, <title>, or <script> inside <head>. The Vitest ` +
        `assertNotInDom companion (scope=document.body) does not catch this class ` +
        `of regression.`,
    );
  }

  if (escaped !== token && headHtml.includes(escaped)) {
    throw new Error(
      `head-injection token-absence assertion failed: HTML-entity-escaped form ` +
        `of sensitive value ${sentinel} appeared in <head>.innerHTML — ` +
        `production code is leaking via an attribute whose serializer rewrote ` +
        `the value's HTML-special chars.`,
    );
  }
}

/**
 * Summarize a sensitive value for inclusion in error messages without
 * leaking the full string into CI logs. Returns `"<sensitive value of
 * length N starting 'XXXX' ending 'YYYY'>"` for values long enough to
 * disambiguate; for short values (≤ 8 chars, e.g. when a smoke spec
 * deliberately uses a short fixture), returns just the length to
 * avoid a near-full disclosure via the prefix + suffix.
 *
 * Lifted-and-mirrored from `frontend/src/test/security.ts:summarize`
 * — same contract on both tiers so the failure message stays safe
 * regardless of which helper fires.
 */
function summarize(value: string): string {
  if (value.length <= 8) {
    return `<sensitive value of length ${value.length}>`;
  }
  const prefix = value.slice(0, 4);
  const suffix = value.slice(-4);
  return `<sensitive value of length ${value.length} starting "${prefix}" ending "${suffix}">`;
}

/**
 * Escape the four HTML-special characters that the DOM serializer
 * rewrites when generating innerHTML from a tree. Deliberately NOT a
 * full HTML-entity encoder — only the four characters that the DOM
 * SERIALIZATION algorithm produces in attribute or text-node content.
 *
 * Lifted from `frontend/src/test/security.ts:escapeHtml` — both
 * tiers must agree on the escape set, otherwise a leak via `&` (the
 * realistic dominant case) would be caught by one tier and missed by
 * the other.
 */
function escapeHtml(value: string): string {
  return value
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}
