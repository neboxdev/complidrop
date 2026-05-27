/**
 * No-secret-in-DOM assertion.
 *
 * For tests that need to verify a session token, portal token, or
 * other sensitive value is NOT rendered into the DOM. Two scans are
 * required because:
 *
 * - `textContent` covers what the user (or a screen-reader) sees.
 * - `innerHTML` covers attribute values, hidden nodes, and any node
 *   whose `display: none` ancestor excludes it from `textContent`.
 *
 * Skipping either scan leaves a hole the next contributor will fall
 * into. The harness lift exists so future tests reach for the helper
 * by import, not by copy-paste with one of the two scans dropped.
 *
 * ## Scope limits (what this helper does NOT cover)
 *
 * - `<head>` injection (analytics meta tags, page titles set via
 *   `next/head`, etc.). Component tests render into `document.body`;
 *   chase head leaks in the Playwright E2E layer.
 * - `localStorage` / `sessionStorage` / `window.*`. The name says
 *   `InDom` — non-DOM channels are explicitly out of scope.
 * - HTML-special character escape mismatch: if `value` contains
 *   `<`, `>`, `&`, or `"`, the helper scans BOTH the literal `value`
 *   AND its HTML-entity-escaped form so a leak through attribute or
 *   text serialization is still caught. (Production portal tokens
 *   are URL-safe base64 — no special chars — so the escaped scan is
 *   only relevant if a future caller passes a different shape.)
 * - Shadow DOM. None today; if a future component uses shadow roots,
 *   the helper needs a `scanShadow` opt.
 */

/**
 * Asserts that `value` does NOT appear in `root.textContent` OR
 * `root.innerHTML` (or in HTML-entity-escaped forms of either).
 * Use it for session / portal / credential strings that the
 * production code must NEVER render into the DOM.
 *
 * Defaults to `document.body` — appropriate for component tests where
 * RTL renders into the body. The `<head>` is deliberately out of
 * scope; chase head-injection leaks in the E2E layer.
 *
 * On failure, the error message identifies WHICH scan caught the leak
 * but reports only a length + 4-char prefix + 4-char suffix sentinel
 * of `value` — NOT the full value. A real security regression on a
 * production-shaped token must not be the same path that discloses
 * the token to CI logs.
 *
 * @throws Error if `value` is found in either scan.
 */
export function assertNotInDom(
  value: string,
  root: HTMLElement = document.body,
): void {
  const text = root.textContent ?? "";
  // Escape value's HTML-special chars so the innerHTML scan ALSO
  // catches leaks where the serialized DOM rewrites `<`/`>`/`&`/`"`
  // (token containing those chars would otherwise round-trip as
  // `&lt;`/`&gt;`/`&amp;`/`&quot;` and `.includes(value)` would
  // miss). Always check both — the literal form catches plain text
  // leaks; the escaped form catches attribute-value leaks where
  // browsers/jsdom serialize specials.
  const escaped = escapeHtml(value);
  const inText = text.includes(value);
  const html = root.innerHTML;
  const inHtmlLiteral = html.includes(value);
  const inHtmlEscaped = escaped !== value && html.includes(escaped);

  if (inText) {
    throw new Error(
      `assertNotInDom: ${summarize(value)} appeared in root.textContent — ` +
        `production code is rendering a sensitive string as visible copy ` +
        `or as text inside an element.`,
    );
  }
  if (inHtmlLiteral || inHtmlEscaped) {
    const escapedNote = inHtmlEscaped && !inHtmlLiteral
      ? " (HTML-entity-escaped form found — leak via attribute serialization)"
      : "";
    throw new Error(
      `assertNotInDom: ${summarize(value)} appeared in root.innerHTML but NOT ` +
        `in root.textContent — production code is leaking the value through ` +
        `an attribute (aria-label, data-*, title, value=, etc.) or a hidden ` +
        `node${escapedNote}. Scan both surfaces to catch this class of leak.`,
    );
  }
}

/**
 * Summarize a sensitive value for inclusion in error messages without
 * leaking the full string into CI logs. Returns `"<sensitive value of
 * length N starting with 'XXXX' ending 'YYYY'>"` for values long enough
 * to disambiguate; for short values (<= 8 chars, e.g. when a test
 * deliberately uses a short fixture), returns just the length to avoid
 * a near-full disclosure via the prefix+suffix.
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
 */
function escapeHtml(value: string): string {
  return value
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}
