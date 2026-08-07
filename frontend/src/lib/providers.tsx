"use client";

import { QueryClientProvider } from "@tanstack/react-query";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { Toaster } from "sonner";
import { createQueryClient } from "./query-client";
import { initAnalytics } from "./analytics";

/**
 * The one route whose URL is itself a bearer credential — `/portal/{token}`,
 * where holding the string is what authorises the upload (#404, ADR 0037
 * Amendments 2–3).
 *
 * Everything else in the app puts identifiers in its URLs. Only here is the URL
 * the secret, which is why this route gets an invariant rather than a
 * redaction: `sanitizeUrl` covers `capture()`, `advanced_disable_flags` covers
 * `/flags`, `capture_heatmaps: false` covers the `location.href`-keyed buffer —
 * three per-channel promises against a dependency that keeps adding channels,
 * and round 2 of #404 found two of them only after round 1 had shipped.
 *
 * This predicate answers "is the tab HERE"; {@link contextHeldCredentialInUrl}
 * answers "has this tab EVER been here". The invariant needs both — round 3
 * found that the first alone lapses the moment the SDK is live in the same JS
 * context.
 */
function isCredentialInUrlRoute(pathname: string | null): boolean {
  return pathname === "/portal" || (pathname?.startsWith("/portal/") ?? false);
}

/**
 * Has THIS browsing context ever rendered the credential-bearing route?
 *
 * Module scope, so it lives exactly as long as the JS context does — one tab,
 * until a full document load resets it. That is the same lifetime as the thing
 * it guards: posthog-js is a module-global singleton, `initAnalytics` sets a
 * module-global `initialized`, and NOTHING here de-initialises or opts out. So
 * a gate that asks only "where is the tab NOW" stops covering the portal the
 * instant the SDK goes live anywhere in the same context (#404 round 3, ADR
 * 0037 Amendment 3): on the way back the effect returns early while the live SDK
 * carries on against the portal URL. Read off posthog-js 1.369.2, not assumed —
 * `_handle_unload` is bound to `pagehide` at init and `capture_pageleave: true`
 * makes it capture `$pageleave` from `window.location`; `autocapture` defaults
 * to `true` and `Autocapture.isEnabled`'s wait-for-the-server guard is skipped
 * precisely because our `advanced_disable_flags: true` makes
 * `_shouldDisableFlags()` true, so it runs on client config alone; and
 * `persistence: 'localStorage+cookie'` keeps rewriting the cookie the portal
 * notice denies.
 *
 * The round trip is not hypothetical — it is the flow ADR 0054's own notice
 * recommends ("as described in our Privacy Policy"). That link is now a plain
 * `<a>` precisely so it leaves this context (ADR 0054 Amendment 2), and this
 * flag is the belt to that brace: two mechanisms, either of which alone keeps
 * the invariant, in the shape #404 already used for the heatmaps channel (key
 * sanitisation AND `capture_heatmaps: false`).
 *
 * **The cost is real and recorded.** A customer who opens a portal link in the
 * same tab loses PostHog for the rest of that tab's session, and the vendor's
 * `/privacy` visit is not measured either. Accepted: the alternative is a
 * bearer credential in a third party's store, and every route is measured again
 * on the next hard load.
 */
let contextHeldCredentialInUrl = false;

export function Providers({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const [client] = useState(() =>
    createQueryClient({
      queries: {
        retry: 1,
        refetchOnWindowFocus: false,
        staleTime: 30_000,
      },
      mutations: { retry: 0 },
    }),
  );

  // Gated on the pathname AND on {@link contextHeldCredentialInUrl}: "is the tab
  // here now" OR "has this tab ever been here". The second half is what makes the
  // invariant hold across a soft navigation away and back — see the flag's own
  // doc for the mechanism and the accepted cost. A tab that never touches the
  // portal is unaffected, and `initAnalytics` is idempotent, so re-running it on
  // later navigations there is a no-op.
  useEffect(() => {
    if (isCredentialInUrlRoute(pathname)) {
      contextHeldCredentialInUrl = true;
      return;
    }
    if (contextHeldCredentialInUrl) return;
    initAnalytics();
  }, [pathname]);

  // Toasts sit top-right on desktop, but on a phone that spot covers the sticky
  // top bar (hamburger + logo) — live-confirmed interception (#318 FP-047). Move
  // them to bottom-center on coarse (touch) pointers, where nothing is occluded
  // and they're within thumb reach. Defaults to top-right for SSR / the first
  // paint, then settles on the client; matchMedia is guarded for non-browser envs.
  const [toastPosition, setToastPosition] = useState<"top-right" | "bottom-center">("top-right");
  useEffect(() => {
    if (typeof window === "undefined" || !window.matchMedia) return;
    const mq = window.matchMedia("(pointer: coarse)");
    const apply = () => setToastPosition(mq.matches ? "bottom-center" : "top-right");
    apply();
    mq.addEventListener("change", apply);
    return () => mq.removeEventListener("change", apply);
  }, []);

  return (
    <QueryClientProvider client={client}>
      {children}
      <Toaster richColors position={toastPosition} />
    </QueryClientProvider>
  );
}
