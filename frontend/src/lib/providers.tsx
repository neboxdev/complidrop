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
 * Amendment 2).
 *
 * Everything else in the app puts identifiers in its URLs. Only here is the URL
 * the secret, which is why this route gets an invariant rather than a
 * redaction: `sanitizeUrl` covers `capture()`, `advanced_disable_flags` covers
 * `/flags`, `capture_heatmaps: false` covers the `location.href`-keyed buffer —
 * three per-channel promises against a dependency that keeps adding channels,
 * and round 2 of #404 found two of them only after round 1 had shipped.
 */
function isCredentialInUrlRoute(pathname: string | null): boolean {
  return pathname === "/portal" || (pathname?.startsWith("/portal/") ?? false);
}

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

  // Gated on the PATHNAME, not on "has ever been on the portal": a vendor who
  // follows the notice's Privacy Policy link is on an ordinary route from that
  // click on, and every other route is deliberately unaffected. `initAnalytics`
  // is idempotent, so re-running it on later navigations is a no-op.
  useEffect(() => {
    if (isCredentialInUrlRoute(pathname)) return;
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
