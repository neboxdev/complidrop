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
 *
 * The History-API bridge gets its own block. It had no direct coverage at all
 * while it was modelled as synchronous, so nothing contradicted that model when
 * it turned out to be wrong — the page suite could only observe the bridge
 * through a component, where a page bug and a harness bug look identical.
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

  it("drops a subscriber that leaked from a test that threw before unsubscribing", async () => {
    // `cleanup()` unmounts components (which unsubscribes them) before this
    // runs, so this only bites a test that subscribed BY HAND — the documents
    // scenario-A test does — and threw before its `unsubscribe()`. Without the
    // clear, that callback survives and fires inside the next test.
    const leaked = vi.fn();
    subscribeNavigation(leaked);
    resetNavigation();

    setNavigationState({ searchParams: { status: "Expired" } });
    expect(leaked).not.toHaveBeenCalled();
  });
});

describe("the History-API bridge", () => {
  // The bridge was originally modelled as fully synchronous, which is what let
  // a page compose filter writes on a transition-deferred value and still pass
  // its own regression tests (#370, second review pass). These pin the split.

  it("moves window.location synchronously but defers the router snapshot", async () => {
    setNavigationState({ pathname: "/documents", searchParams: {} });

    window.history.replaceState(null, "", "/documents?status=Expired");

    // The address bar is already there…
    expect(window.location.search).toBe("?status=Expired");
    // …while `useSearchParams()`'s source is not. Next wraps its own sync in
    // startTransition, so a component reading the hook on the next line still
    // sees the OLD query string.
    expect(navState.searchParams.get("status")).toBeNull();

    await settle();
    expect(navState.searchParams.get("status")).toBe("Expired");
    expect(navState.pathname).toBe("/documents");
  });

  it("notifies subscribers only once the deferred half lands", async () => {
    const onChange = vi.fn();
    const unsubscribe = subscribeNavigation(onChange);

    window.history.replaceState(null, "", "/documents?a=1");
    expect(onChange).not.toHaveBeenCalled();

    await settle();
    expect(onChange).toHaveBeenCalled();
    unsubscribe();
  });

  it("bridges pushState as well as replaceState", async () => {
    window.history.pushState(null, "", "/documents?b=2");
    expect(window.location.search).toBe("?b=2");
    await settle();
    expect(navState.searchParams.get("b")).toBe("2");
  });

  it("cancels a pending history commit at resetNavigation, like a router one", async () => {
    window.history.replaceState(null, "", "/documents?leak=1");
    resetNavigation();
    await settle();
    // Neither half may survive into the next test.
    expect(navState.searchParams.toString()).toBe("");
    expect(window.location.search).toBe("");
  });

  it("seeds the router snapshot BEFORE the address bar, like a router navigation", async () => {
    // `setNavigationState` models an external/router URL change, so it splits
    // the way a router navigation does — the opposite way to a History-API
    // write. The snapshot is authoritative during the render; `window.location`
    // catches up afterwards (Next moves it in HistoryUpdater's
    // useInsertionEffect, a commit-phase effect).
    //
    // This ordering is load-bearing, not incidental: a page that prefers
    // `window.location` over `useSearchParams()` renders the PREVIOUS route's
    // query for a router navigation and is never corrected, because Next's
    // internal history write carries `__NA` and dispatches nothing.
    setNavigationState({ pathname: "/documents", searchParams: { vendor: "v1" } });

    // Snapshot first…
    expect(navState.pathname).toBe("/documents");
    expect(navState.searchParams.get("vendor")).toBe("v1");
    // …address bar not yet.
    expect(new URLSearchParams(window.location.search).get("vendor")).toBeNull();

    await settle();
    expect(window.location.pathname).toBe("/documents");
    expect(new URLSearchParams(window.location.search).get("vendor")).toBe("v1");
  });

  it("moves window.location when a router navigation commits", async () => {
    navState.router.replace("/documents?status=Expired");
    await settle();
    expect(window.location.search).toBe("?status=Expired");
  });
});
