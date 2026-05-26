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
 * the no-more-polling contract), `sequencedJsonOk` returns the LAST
 * response again instead of throwing "ran out of responses".
 *
 * ## Polling-test gotchas (not handled here)
 *
 * - `vi.useFakeTimers({ shouldAdvanceTime: true })` is REQUIRED for
 *   RTL's `waitFor` to work — without it the `waitFor` polling loop
 *   itself blocks on the fake-timer queue.
 * - Fake timers must be activated BEFORE the component mounts so the
 *   `refetchInterval` is scheduled on the fake queue. Activate them in
 *   a `beforeEach`, not inside the test body.
 * - Call counts asserted via the `assertedCallCount` variable rather
 *   than re-reading a closure — pass a counter ref if you need to pin
 *   the exact number of fetches.
 */
import { HttpResponse } from "msw";
import { jsonOk } from "./helpers";

/**
 * Returns an MSW handler that sequences through the given responses on
 * successive calls. Once exhausted, the LAST response is returned
 * indefinitely (matches the "terminal state stays terminal" contract
 * of refetchInterval-driven polling).
 *
 * Usage:
 *   server.use(
 *     http.get(url("/api/documents/:id"), sequencedJsonOk(
 *       makeDocumentDetail({ extractionStatus: "Pending" }),
 *       makeDocumentDetail({ extractionStatus: "Processing" }),
 *       makeDocumentDetail({ extractionStatus: "Completed" }),
 *     )),
 *   );
 *
 * @throws RangeError on construction if `responses` is empty — a
 *   handler with zero responses is always a programming error.
 */
export function sequencedJsonOk<T>(...responses: T[]): () => HttpResponse {
  if (responses.length === 0) {
    throw new RangeError(
      "sequencedJsonOk: at least one response is required",
    );
  }
  let calls = 0;
  return () => {
    // Index clamps to the last response after it's exhausted so a
    // terminal-state poll-stop test that does one extra advance to
    // confirm "no further polls" gets the same terminal payload again.
    const index = Math.min(calls, responses.length - 1);
    calls++;
    return jsonOk(responses[index]);
  };
}
