"use client";

/**
 * Accessible on/off switch built on the Base UI `Switch` primitive — which
 * renders `role="switch"` + `aria-checked` and handles keyboard (Space/Enter)
 * for free. Consumers MUST pass a per-instance `aria-label` (the switch has no
 * visible text of its own). (#189)
 *
 * A11y specifics the bare <button> it replaces lacked:
 *   - role="switch" + aria-checked (Base UI)
 *   - a visible focus ring
 *   - a NON-COLOR on/off cue: the thumb slides left↔right AND a check/dash glyph
 *     swaps in, so the state survives grayscale / color-blindness, not hue alone
 *   - a ≥44px pointer hit area (via the inset ::before) even though the track is
 *     visually smaller
 */
import * as React from "react";
import { Switch } from "@base-ui/react/switch";
import { Check, Minus } from "lucide-react";
import { cn } from "@/lib/utils";

export function ToggleSwitch({
  className,
  ...props
}: React.ComponentProps<typeof Switch.Root>) {
  return (
    <Switch.Root
      className={cn(
        "group relative inline-flex h-6 w-11 shrink-0 cursor-pointer items-center rounded-full p-0.5 transition-colors",
        "bg-slate-400 data-[checked]:bg-sky-600",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sky-500/50 focus-visible:ring-offset-2",
        "disabled:cursor-not-allowed disabled:opacity-60",
        // Expand the pointer hit area to ≥44px without enlarging the visual track.
        "before:absolute before:left-1/2 before:top-1/2 before:h-11 before:w-11 before:-translate-x-1/2 before:-translate-y-1/2 before:content-['']",
        "motion-reduce:transition-none",
        className,
      )}
      {...props}
    >
      <Switch.Thumb
        className={cn(
          "flex h-5 w-5 items-center justify-center rounded-full bg-white text-slate-400 shadow transition-transform",
          "data-[checked]:translate-x-5 data-[checked]:text-sky-600",
          "motion-reduce:transition-none",
        )}
      >
        {/* Non-color cue: dash when off, check when on (survives grayscale). */}
        <Check className="hidden h-3 w-3 group-data-[checked]:block" aria-hidden />
        <Minus className="block h-3 w-3 group-data-[checked]:hidden" aria-hidden />
      </Switch.Thumb>
    </Switch.Root>
  );
}
