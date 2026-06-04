"use client";

import { useState } from "react";
import { Lightbulb, X } from "lucide-react";
import { isTipDismissed, dismissTip } from "@/lib/onboarding";
import { cn } from "@/lib/utils";

/**
 * A dismissible, device-local first-visit tip (#191). Once dismissed it stays
 * hidden (localStorage), until "Restart tour" in Settings clears every tip.
 *
 * The dashboard routes that host these tips are client-only (the (dashboard)
 * layout renders a loading shell until the session resolves, so the page — and
 * this tip — never render on the server). That lets us read the dismissed state
 * directly in a lazy `useState` initializer (pure, runs once at mount) instead of
 * an effect — no SSR mismatch to guard against, and no set-state-in-effect.
 */
export function PageTip({
  id,
  title,
  children,
  className,
}: {
  id: string;
  title: string;
  children: React.ReactNode;
  className?: string;
}) {
  const [dismissed, setDismissed] = useState(() => isTipDismissed(id));

  if (dismissed) return null;

  return (
    <div
      role="note"
      className={cn(
        "flex items-start gap-3 rounded-lg border border-sky-200 bg-sky-50 px-4 py-3 text-sm",
        className,
      )}
    >
      <Lightbulb className="mt-0.5 h-4 w-4 shrink-0 text-sky-600" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <p className="font-medium text-sky-900">{title}</p>
        <div className="mt-0.5 text-sky-800">{children}</div>
      </div>
      <button
        type="button"
        aria-label="Dismiss tip"
        onClick={() => {
          dismissTip(id);
          setDismissed(true);
        }}
        className="shrink-0 rounded p-1 text-sky-500 hover:bg-sky-100 hover:text-sky-700 pointer-coarse:min-h-11 pointer-coarse:min-w-11"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}
