/**
 * The `next/navigation` harness itself (#370).
 *
 * `navigation.ts` grew real logic — href parsing, a subscriber fan-out, and a
 * deliberately DEFERRED commit — and every component suite now renders through
 * it, so a defect here shows up as a confusing failure somewhere else. Every
 * other helper in `src/test/` ships a colocated test (ADR 0019 set that
 * precedent for `abort-signal-bridge`); this is navigation's.
 *
 * The page tests only ever navigate to `/documents?...`, so the branches
 * pinned here — absolute URLs, hash-only and query-only hrefs, non-string
 * input, unsubscribe, and cross-test timer cancellation — are otherwise
 * unexercised.
 */
import { afterEach, describe, it, expect, vi } from "vitest";
import { navState, resetNavigation, setNavigationState, subscribeNavigation } from "./navigation";

afterEach(() => resetNavigation());

/** The commit is deferred by a macrotask on purpose — wait one out. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

describe("applyNavigation (via router.push/replace)", () => {
  it("applies a relative href's path and query", async () => {
    navState.router.replace("/documents?status=Expired");
    await settle();
    expect(navState.pathname).toBe("/documents");
    expect(navState.searchParams.get("status")).toBe("Expired");
  });

  it("defers the commit so the caller's own re-render sees the OLD url", async () => {
    setNavigationState({ pathname: "/documents", searchParams: { status: "Expired" } });

    navState.router.replace("/documents");
    // Synchronously after the call — the transition has not landed. This lag is
    // the whole point: #370's scenario A lived in exactly this window, and a
    // synchronous mock lets that bug pass its own regression test.
    expect(navState.searchParams.get("status")).toBe("Expired");

    await settle();
    expect(navState.searchParams.toString()).toBe("");
  });

  it("clears the query when a path-only href is navigated to", async () => {
    setNavigationState({ searchParams: { vendor: "v1" } });
    navState.router.replace("/documents");
    await settle();
    expect(navState.searchParams.toString()).toBe("");
  });

  it("keeps the path for a query-only href", async () => {
    setNavigationState({ pathname: "/vendors", searchParams: {} });
    navState.router.push("?page=2");
    await settle();
    expect(navState.pathname).toBe("/vendors");
    expect(navState.searchParams.get("page")).toBe("2");
  });

  it("leaves both path and query alone for a hash-only href", async () => {
    setNavigationState({ pathname: "/documents", searchParams: { status: "Expired" } });
    navState.router.push("#main");
    await settle();
    expect(navState.pathname).toBe("/documents");
    expect(navState.searchParams.get("status")).toBe("Expired");
  });

  it("strips the hash from a path href", async () => {
    navState.router.push("/documents?status=Expired#row-3");
    await settle();
    expect(navState.pathname).toBe("/documents");
    expect(navState.searchParams.get("status")).toBe("Expired");
  });

  it("parses an absolute URL", async () => {
    navState.router.replace("https://app.example.com/documents?vendor=v9");
    await settle();
    expect(navState.pathname).toBe("/documents");
    expect(navState.searchParams.get("vendor")).toBe("v9");
  });

  it("ignores a non-string href instead of throwing", async () => {
    setNavigationState({ pathname: "/documents" });
    expect(() => navState.router.push(undefined)).not.toThrow();
    await settle();
    expect(navState.pathname).toBe("/documents");
  });

  it("stays an assertable spy while navigating", async () => {
    navState.router.replace("/documents?type=permit");
    expect(navState.router.replace).toHaveBeenCalledWith("/documents?type=permit");
    await settle();
    expect(navState.searchParams.get("type")).toBe("permit");
  });

  it("rebuilds searchParams as a new object so useSyncExternalStore re-renders", async () => {
    const before = navState.searchParams;
    navState.router.replace("/documents?status=Expired");
    await settle();
    expect(navState.searchParams).not.toBe(before);
  });
});

describe("subscribeNavigation", () => {
  it("notifies on a navigation and stops after unsubscribe", async () => {
    const onChange = vi.fn();
    const unsubscribe = subscribeNavigation(onChange);

    navState.router.replace("/documents?status=Expired");
    await settle();
    expect(onChange).toHaveBeenCalled();

    unsubscribe();
    onChange.mockClear();
    navState.router.replace("/documents");
    await settle();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("notifies on setNavigationState so an external URL change re-renders", () => {
    const onChange = vi.fn();
    const unsubscribe = subscribeNavigation(onChange);
    setNavigationState({ searchParams: { status: "Expired" } });
    expect(onChange).toHaveBeenCalled();
    unsubscribe();
  });
});

describe("resetNavigation", () => {
  it("cancels a deferred commit so a navigation cannot cross a test boundary", async () => {
    // The real shape of this leak: a test ends right after asserting on a
    // push spy (WelcomeModal.test.tsx does exactly that), the afterEach reset
    // runs on microtasks, and the queued macrotask lands inside the NEXT test.
    navState.router.push("/login");
    resetNavigation();

    await settle();
    expect(navState.pathname).toBe("/");
    expect(navState.searchParams.toString()).toBe("");
  });

  it("does not notify subscribers from a cancelled commit", async () => {
    const onChange = vi.fn();
    const unsubscribe = subscribeNavigation(onChange);
    navState.router.push("/login");
    resetNavigation();
    await settle();
    expect(onChange).not.toHaveBeenCalled();
    unsubscribe();
  });
});
