/**
 * Pins the sequencedJsonOk contract added in #82.
 *
 * Three invariants matter for downstream tests:
 *   1. Calling with zero responses throws at construction time
 *      (RangeError) — a handler with no responses is always a
 *      programming error, fail loudly at the call site rather than
 *      defer the failure to the first MSW request.
 *   2. Sequence-then-clamp semantics: yields responses[0..N-1] in
 *      order, then returns responses[N-1] indefinitely. Mirrors the
 *      `refetchInterval` predicate's "terminal state stays terminal"
 *      contract.
 *   3. Per-invocation counter: each `sequencedJsonOk(...)` call
 *      returns a handler with its OWN closed-over counter. Two
 *      handlers in the same test don't share state.
 *
 * A regression that broke any of these would silently degrade every
 * downstream polling test (the migrated documents/useDocuments tests
 * would still pass for the WRONG reason — they'd be reading whichever
 * payload the broken helper happens to emit). Pin them here at the
 * fast tier.
 */
import { describe, it, expect } from "vitest";
import { sequencedJsonOk } from "./polling";

describe("sequencedJsonOk — handler contract (#82)", () => {
  it("throws RangeError synchronously at construction time when called with no responses", () => {
    expect(() => sequencedJsonOk()).toThrow(RangeError);
    expect(() => sequencedJsonOk()).toThrow(/at least one response/i);
  });

  it("yields each response in order, then clamps to the LAST one indefinitely", async () => {
    const handler = sequencedJsonOk({ tag: "a" }, { tag: "b" }, { tag: "c" });
    const bodies = await Promise.all(
      [0, 1, 2, 3, 4].map(async () => {
        const res = handler();
        // jsonOk wraps in an ApiEnvelope; read the inner data.
        const env = (await res.json()) as { data: { tag: string } };
        return env.data.tag;
      }),
    );
    // First three calls yield in order, then index clamps to length-1.
    expect(bodies).toEqual(["a", "b", "c", "c", "c"]);
  });

  it("yields the single response indefinitely when constructed with exactly one", async () => {
    // Edge case: clamp on a single-element sequence. Verifies the
    // Math.min(calls, length - 1) bound doesn't underflow.
    const handler = sequencedJsonOk({ only: true });
    const bodies = await Promise.all(
      [0, 1, 2].map(async () => {
        const env = (await handler().json()) as { data: { only: boolean } };
        return env.data.only;
      }),
    );
    expect(bodies).toEqual([true, true, true]);
  });

  it("two handlers in the same test have INDEPENDENT counters", async () => {
    // Pins per-invocation closure scoping: a regression that
    // accidentally hoisted `calls` to module scope (or shared state
    // between handlers) would have both sequences advance together
    // and break every test that mocks two endpoints at once.
    const h1 = sequencedJsonOk("alpha-1", "alpha-2", "alpha-3");
    const h2 = sequencedJsonOk("beta-1", "beta-2", "beta-3");

    const a1 = (await h1().json()) as { data: string };
    const a2 = (await h1().json()) as { data: string };
    // h2 has not been called yet — its counter MUST still be at 0,
    // not at 2 (which would happen with shared state).
    const b1 = (await h2().json()) as { data: string };
    const a3 = (await h1().json()) as { data: string };
    const b2 = (await h2().json()) as { data: string };

    expect(a1.data).toBe("alpha-1");
    expect(a2.data).toBe("alpha-2");
    expect(a3.data).toBe("alpha-3");
    expect(b1.data).toBe("beta-1");
    expect(b2.data).toBe("beta-2");
  });
});
