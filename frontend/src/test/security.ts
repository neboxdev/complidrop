/**
 * No-secret-in-DOM assertion.
 *
 * For tests that need to verify a session token, portal token, or
 * other sensitive value is NOT rendered into the DOM — by ANY mechanism
 * (visible copy, aria-label, debug ID, tooltip, data-attribute on a
 * hidden element). Two scans are required because:
 *
 * - `textContent` covers what the user (or a screen-reader) sees.
 * - `innerHTML` covers attribute values, hidden nodes, and any node
 *   whose `display: none` ancestor excludes it from `textContent`.
 *
 * Skipping either scan leaves a hole the next contributor will fall
 * into. The harness lift exists so future tests reach for the helper
 * by import, not by copy-paste with one of the two scans dropped.
 *
 * Scope is `document.body` by default — `<head>` injection paths
 * (analytics meta tags, page titles set via `next/head`, etc.) are
 * document-level concerns appropriate for the E2E suite, not component
 * tests. Pass an explicit `root` (e.g. a specific portal container) to
 * narrow further.
 */

/**
 * Asserts that `value` does NOT appear in `root.textContent` OR
 * `root.innerHTML`. Use it for session / portal / credential strings
 * that the production code must NEVER render into the DOM.
 *
 * Defaults to `document.body` — appropriate for component tests where
 * RTL renders into the body. The `<head>` is deliberately out of
 * scope; chase head-injection leaks in the E2E layer.
 *
 * @throws AssertionError (via the test runner) if `value` is found in
 *   either scan, with a message identifying which scan caught the leak.
 */
export function assertNotInDom(
  value: string,
  root: HTMLElement = document.body,
): void {
  const text = root.textContent ?? "";
  if (text.includes(value)) {
    throw new Error(
      `assertNotInDom: value "${value}" appeared in root.textContent — ` +
        `production code is rendering a sensitive string as visible copy ` +
        `or as text inside an element.`,
    );
  }
  if (root.innerHTML.includes(value)) {
    throw new Error(
      `assertNotInDom: value "${value}" appeared in root.innerHTML but NOT ` +
        `in root.textContent — production code is leaking the value through ` +
        `an attribute (aria-label, data-*, title, value=, etc.) or a hidden ` +
        `node. Scan both surfaces to catch this class of leak.`,
    );
  }
}
