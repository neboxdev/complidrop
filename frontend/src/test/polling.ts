/**
 * Sequenced MSW handlers for polling-state-transition tests.
 *
 * Three component / hook tests previously hand-rolled the same shape
 * to drive a polling query through a series of responses:
 *
 *   let calls = 0;
 *   server.use(
 *     http.get(url("/api/..."), () => {
 *       calls++;
 *       return jsonOk(calls === 1 ? pendingResponse : completedResponse);
 *     }),
 *   );
 *
 * Each one re-derived the same logic: increment a counter, branch on
 * its value, fall through to the terminal state on subsequent calls.
 * Lift it into one tested helper so future polling tests pick the
 * shape up by import, not by copy.
 *
 * The terminal-state-persists semantics matches how the underlying
 * `refetchInterval` predicate works: once the response settles on a
 * terminal status (`Completed` / `Failed`), the predicate returns
 * `false` and polling stops naturally — but if the test triggers ONE
 * MORE poll (e.g. via `vi.advanceTimersByTimeAsync(...)` to confirm
 * the no-more-polling contract), the helper returns the LAST
 * response again instead of throwing "ran out of responses".
 *
 * ## Two variants
 *
 * - `sequencedJsonOk(...payloads)` — success-only sugar; wraps every
 *   element in `jsonOk(...)`. Cleanest for the common polling-
 *   transition case where every step is a 200.
 * - `sequencedResponses(...factories)` — general form; each element
 *   is a `() => Response` factory composable with `jsonOk` /
 *   `jsonError` (e.g. `() => jsonError("server.error", "...",
 *   { status: 500 })`). Required for mixed success/error sequences
 *   like the documents/page retry-on-5xx test (500 → 200) where
 *   `sequencedJsonOk` can't express the failure step. (#124)
 *
 * ## Polling-test gotchas (not handled here)
 *
 * - `vi.useFakeTimers({ shouldAdvanceTime: true })` is REQUIRED for
 *   RTL's `waitFor` to work — without it the `waitFor` polling loop
 *   itself blocks on the fake-timer queue.
 * - Fake timers must be activated BEFORE the component mounts so the
 *   `refetchInterval` is scheduled on the fake queue. Activate them in
 *   a `beforeEach`, not inside the test body.
 * - Call counts: the helper's internal counter is deliberately NOT
 *   exposed (keeps the signature simple). Tests that need to assert on
 *   the number of fetches keep an external `let calls = 0; calls++`
 *   inside the handler wrapper — see the migrated sites in
 *   `useDocuments.test.tsx` / `documents/page.test.tsx` for the
 *   canonical shape.
 */
import { jsonOk } from "./helpers";

/**
 * Returns an MSW handler that sequences through the given responses on
 * successive calls. Once exhausted, the LAST response is returned
 * indefinitely (matches the "terminal state stays terminal" contract
 * of refetchInterval-driven polling).
 *
 * Usage (canonical — wrap to keep an external `calls` counter for
 * call-count assertions, since the helper's internal counter is
 * intentionally not exposed):
 *
 *   let calls = 0;
 *   const seq = sequencedJsonOk(
 *     makeDocumentDetail({ extractionStatus: "Pending" }),
 *     makeDocumentDetail({ extractionStatus: "Processing" }),
 *     makeDocumentDetail({ extractionStatus: "Completed" }),
 *   );
 *   server.use(
 *     http.get(url("/api/documents/:id"), () => {
 *       calls++;
 *       return seq();
 *     }),
 *   );
 *   // ... drive timers ...
 *   expect(calls).toBeGreaterThanOrEqual(3);
 *
 * @throws RangeError on construction if `responses` is empty — a
 *   handler with zero responses is always a programming error.
 */
export function sequencedJsonOk<T>(...responses: T[]): () => Response {
  // Empty-check is duplicated here (not delegated to sequencedResponses)
  // so the call-site RangeError message reads naturally — "at least one
  // response is required" matches what a sequencedJsonOk caller meant
  // to say, even though the runtime behavior delegates to
  // sequencedResponses below. (#124 review)
  if (responses.length === 0) {
    throw new RangeError(
      "sequencedJsonOk: at least one response is required",
    );
  }
  // Delegate to sequencedResponses so the terminal-clamp + counter +
  // closure-scoping invariants live in ONE place. A bug in those
  // invariants now surfaces in BOTH helpers' contract tests, removing
  // the "the two helpers silently drift in subtle ways" failure mode
  // the parallel-bodies pattern would have invited. (#124 review)
  return sequencedResponses(...responses.map((r) => () => jsonOk(r)));
}

/**
 * Generalized `sequencedJsonOk` for mixed success/error sequences.
 * Takes Response FACTORIES — not pre-built Responses — so each
 * invocation produces a FRESH Response (Response body streams are
 * single-use; reusing the same instance across multiple fetches
 * would fail after the first read).
 *
 * Composes naturally with the existing `jsonOk` / `jsonError`
 * helpers via arrow-function wrappers:
 *
 *   let calls = 0;
 *   const seq = sequencedResponses(
 *     () => jsonError("server.error", "DB blip.", { status: 500 }),
 *     () => jsonOk(makeDocumentsResponse({ ... })),
 *   );
 *   server.use(
 *     http.get(url("/api/documents"), () => {
 *       calls++;
 *       return seq();
 *     }),
 *   );
 *
 * Mirrors `sequencedJsonOk`'s closure-counter + terminal-clamp +
 * RangeError semantics exactly — the only contract delta is taking
 * factories instead of payloads. Use `sequencedJsonOk` when every
 * step is a success (cleaner call site); reach for this variant only
 * when the sequence needs to flip between codes.
 *
 * @throws RangeError on construction if `factories` is empty.
 */
export function sequencedResponses(
  ...factories: Array<() => Response>
): () => Response {
  if (factories.length === 0) {
    throw new RangeError(
      "sequencedResponses: at least one factory is required",
    );
  }
  let calls = 0;
  return () => {
    // Same terminal-clamp semantics as sequencedJsonOk: a refetch-
    // interval test that does ONE more advance to confirm the
    // no-more-polling contract gets the last response again instead
    // of running off the end. Factories called fresh on every
    // invocation so the returned Response always has an unread body
    // stream — even on the clamped repeat-calls.
    const index = Math.min(calls, factories.length - 1);
    calls++;
    return factories[index]();
  };
}
