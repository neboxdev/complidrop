/**
 * Pins the sequencedJsonOk + sequencedResponses contracts (#82, #124).
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
 *   3. Per-invocation counter: each `sequencedJsonOk(...)` /
 *      `sequencedResponses(...)` call returns a handler with its OWN
 *      closed-over counter. Two handlers in the same test don't
 *      share state.
 *
 * A regression that broke any of these would silently degrade every
 * downstream polling test (the migrated documents/useDocuments tests
 * would still pass for the WRONG reason — they'd be reading whichever
 * payload the broken helper happens to emit). Pin them here at the
 * fast tier.
 */
import { describe, it, expect } from "vitest";
import { sequencedJsonOk, sequencedResponses } from "./polling";
import { jsonError, jsonOk } from "./helpers";

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

describe("sequencedResponses — handler contract (#124)", () => {
  it("throws RangeError synchronously at construction time when called with no factories", () => {
    expect(() => sequencedResponses()).toThrow(RangeError);
    expect(() => sequencedResponses()).toThrow(/at least one factory/i);
  });

  it("yields each factory's response in order, then clamps to the LAST one indefinitely", async () => {
    // Mirrors sequencedJsonOk's analogous test. Uses three distinct
    // factories so the order assertion catches a wrong-index bug
    // even if every factory returned the same payload shape.
    const handler = sequencedResponses(
      () => jsonOk({ tag: "a" }),
      () => jsonOk({ tag: "b" }),
      () => jsonOk({ tag: "c" }),
    );
    const bodies = await Promise.all(
      [0, 1, 2, 3, 4].map(async () => {
        const res = handler();
        const env = (await res.json()) as { data: { tag: string } };
        return env.data.tag;
      }),
    );
    // First three calls yield in order, then index clamps to length-1.
    expect(bodies).toEqual(["a", "b", "c", "c", "c"]);
  });

  it("yields the single factory's response indefinitely when constructed with exactly one", async () => {
    // Edge case: clamp on a single-element sequence. Verifies the
    // Math.min(calls, length - 1) bound doesn't underflow.
    const handler = sequencedResponses(() => jsonOk({ only: true }));
    const bodies = await Promise.all(
      [0, 1, 2].map(async () => {
        const env = (await handler().json()) as { data: { only: boolean } };
        return env.data.only;
      }),
    );
    expect(bodies).toEqual([true, true, true]);
  });

  it("two handlers in the same test have INDEPENDENT counters", async () => {
    // Same invariant as sequencedJsonOk's analogous test — pinning per
    // -invocation closure scoping so a regression that hoisted `calls`
    // to module scope would surface here.
    const h1 = sequencedResponses(
      () => jsonOk("alpha-1"),
      () => jsonOk("alpha-2"),
      () => jsonOk("alpha-3"),
    );
    const h2 = sequencedResponses(
      () => jsonOk("beta-1"),
      () => jsonOk("beta-2"),
      () => jsonOk("beta-3"),
    );

    const a1 = (await h1().json()) as { data: string };
    const a2 = (await h1().json()) as { data: string };
    const b1 = (await h2().json()) as { data: string };
    const a3 = (await h1().json()) as { data: string };
    const b2 = (await h2().json()) as { data: string };

    expect(a1.data).toBe("alpha-1");
    expect(a2.data).toBe("alpha-2");
    expect(a3.data).toBe("alpha-3");
    expect(b1.data).toBe("beta-1");
    expect(b2.data).toBe("beta-2");
  });

  it("supports mixed jsonOk/jsonError sequences — the documents/page retry-on-5xx shape (#80, #124)", async () => {
    // The whole reason sequencedResponses exists: sequencedJsonOk
    // can't express "first call 500, second call 200" because it
    // wraps every element in jsonOk. This pins the canonical mixed-
    // sequence case via the actual jsonError/jsonOk helpers (not a
    // hand-rolled Response) so a future contributor sees what the
    // composition looks like.
    const handler = sequencedResponses(
      () => jsonError("server.error", "DB blip.", { status: 500 }),
      () => jsonOk({ ok: true }),
    );

    const r1 = handler();
    expect(r1.status).toBe(500);
    const err = (await r1.json()) as { error: { code: string; message: string } };
    expect(err.error.code).toBe("server.error");
    expect(err.error.message).toBe("DB blip.");

    const r2 = handler();
    expect(r2.status).toBe(200);
    const ok = (await r2.json()) as { data: { ok: boolean } };
    expect(ok.data.ok).toBe(true);

    // Terminal clamp on the SUCCESS factory — a third call returns
    // the 200, not a re-throw or an exhausted-sequence error.
    const r3 = handler();
    expect(r3.status).toBe(200);
  });

  it("factories are CALLED on every invocation — Response body streams stay readable across repeat-clamped calls", async () => {
    // Pins the design rationale for taking factories instead of pre-
    // built Responses: a Response body is a single-use stream, so
    // reusing the same Response instance across multiple fetches
    // would fail after the first read. Factory functions sidestep
    // the issue — each call produces a fresh Response. This test
    // would fail under a refactor that cached `factories[i]()` once.
    let invocationCount = 0;
    const handler = sequencedResponses(() => {
      invocationCount++;
      return jsonOk({ n: invocationCount });
    });

    // Three calls — clamped to the single factory. The factory MUST
    // run three times (not once with a cached Response that errors
    // on the second .json() consumption).
    const r1 = (await handler().json()) as { data: { n: number } };
    const r2 = (await handler().json()) as { data: { n: number } };
    const r3 = (await handler().json()) as { data: { n: number } };

    expect(invocationCount).toBe(3);
    expect(r1.data.n).toBe(1);
    expect(r2.data.n).toBe(2);
    expect(r3.data.n).toBe(3);
  });
});
