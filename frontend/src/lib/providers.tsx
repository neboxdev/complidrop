"use client";

import { QueryClientProvider } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Toaster } from "sonner";
import { createQueryClient } from "./query-client";
import { initAnalytics } from "./analytics";

export function Providers({ children }: { children: React.ReactNode }) {
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

  useEffect(() => {
    initAnalytics();
  }, []);

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
