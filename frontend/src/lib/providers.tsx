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

  return (
    <QueryClientProvider client={client}>
      {children}
      <Toaster richColors position="top-right" />
    </QueryClientProvider>
  );
}
