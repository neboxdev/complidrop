"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Dialog } from "@base-ui/react/dialog";
import { ClipboardList, FileUp, ShieldCheck, UserPlus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

// The four-step workflow shown on slide 2 — the same mental model the dashboard
// "Get started" checklist mirrors, so the modal and the checklist stay in lockstep.
const STEPS = [
  { icon: UserPlus, label: "Add a vendor", hint: "The business whose documents you track." },
  { icon: ClipboardList, label: "Set what they must prove", hint: "Pick a requirement checklist for their type." },
  { icon: FileUp, label: "Collect the document", hint: "Upload it yourself, or send a one-click upload link." },
  { icon: ShieldCheck, label: "Read the result", hint: "We pull out the details and flag anything missing or expired." },
];

const SLIDES = [
  {
    title: "Stay audit-ready without the chase",
    description:
      "CompliDrop keeps your vendors' insurance certificates (COIs), licenses, and permits on file — and warns you before any of them expire.",
    showSteps: false,
  },
  {
    title: "Four steps to covered",
    description: "Here's the whole workflow, start to finish:",
    showSteps: true,
  },
  {
    title: "Start with your first vendor",
    description:
      "Add a vendor, choose what they must prove, then upload their document or send them an upload link. We read it and flag anything missing.",
    showSteps: false,
  },
] as const;

/**
 * First-run welcome modal (#191) — a 3-slide, skippable intro built on the app's
 * Base UI Dialog (focus trap, scroll-lock, Escape/backdrop dismissal for free).
 *
 * Controlled: the parent owns `open`. Two distinct close intents (#318 FP-046):
 *   - `onComplete` — an EXPLICIT dismissal (the X / "Skip", or the final CTA):
 *     persists completion so the tour never re-fires.
 *   - `onMinimize` — an INCIDENTAL dismissal (backdrop click or Escape): closes
 *     for now but does NOT persist, so a stray click doesn't end the tour
 *     forever — it re-appears next visit, and "Restart tour" lives in Settings.
 * Base UI reports the close reason via `eventDetails.reason`, so we route the two
 * cases without extra wiring.
 */
export function WelcomeModal({
  open,
  onComplete,
  onMinimize,
}: {
  open: boolean;
  onComplete: () => void;
  onMinimize: () => void;
}) {
  const router = useRouter();
  const [slide, setSlide] = useState(0);
  const last = SLIDES.length - 1;
  const current = SLIDES[slide];

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(nextOpen, eventDetails) => {
        if (nextOpen) return;
        // Reset to slide 0 on close so the next open (e.g. "Restart tour") starts
        // fresh — avoids a reset effect, and every close funnels through here.
        setSlide(0);
        // Backdrop / Escape are incidental → minimize; the X, "Skip", and the
        // final CTA are explicit close-press → complete.
        if (eventDetails.reason === "outside-press" || eventDetails.reason === "escape-key") {
          onMinimize();
        } else {
          onComplete();
        }
      }}
    >
      <Dialog.Portal>
        <Dialog.Backdrop
          className={cn(
            "fixed inset-0 z-50 bg-slate-900/50 transition-opacity duration-150",
            "data-[starting-style]:opacity-0 data-[ending-style]:opacity-0 motion-reduce:transition-none",
          )}
        />
        <Dialog.Popup
          className={cn(
            "fixed left-1/2 top-1/2 z-50 w-[92vw] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl bg-white p-6 shadow-xl outline-none",
            "data-[starting-style]:scale-95 data-[starting-style]:opacity-0 data-[ending-style]:scale-95 data-[ending-style]:opacity-0",
            "transition-[transform,opacity] duration-150 motion-reduce:transition-none",
          )}
        >
          <div className="flex items-start justify-between gap-4">
            <Dialog.Title className="text-lg font-semibold text-sky-900">{current.title}</Dialog.Title>
            <Dialog.Close
              render={
                <button
                  type="button"
                  aria-label="Skip the tour"
                  className="-mr-1 -mt-1 shrink-0 rounded-md p-1.5 text-slate-500 hover:bg-slate-100 hover:text-slate-700 pointer-coarse:min-h-11 pointer-coarse:min-w-11"
                />
              }
            >
              <X className="h-4 w-4" />
            </Dialog.Close>
          </div>

          <Dialog.Description className="mt-2 text-sm text-slate-600">
            {current.description}
          </Dialog.Description>

          {current.showSteps && (
            <ol className="mt-4 space-y-3">
              {STEPS.map((step, i) => (
                <li key={step.label} className="flex items-start gap-3">
                  <span className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-sky-100 text-xs font-semibold text-sky-700">
                    {i + 1}
                  </span>
                  <div className="min-w-0">
                    <p className="flex items-center gap-1.5 text-sm font-medium text-slate-800">
                      <step.icon className="h-4 w-4 text-sky-600" aria-hidden="true" />
                      {step.label}
                    </p>
                    <p className="text-xs text-slate-500">{step.hint}</p>
                  </div>
                </li>
              ))}
            </ol>
          )}

          <div className="mt-6 flex items-center justify-between gap-3">
            <div className="flex items-center gap-1.5" aria-hidden="true">
              {SLIDES.map((s, i) => (
                <span
                  key={s.title}
                  className={cn(
                    "h-1.5 rounded-full transition-all",
                    i === slide ? "w-5 bg-sky-600" : "w-1.5 bg-slate-300",
                  )}
                />
              ))}
            </div>

            <div className="flex items-center gap-2">
              {slide > 0 && (
                <Button variant="outline" size="sm" onClick={() => setSlide((s) => Math.max(0, s - 1))}>
                  Back
                </Button>
              )}
              {slide < last ? (
                <Button size="sm" onClick={() => setSlide((s) => Math.min(last, s + 1))}>
                  Next
                </Button>
              ) : (
                <Dialog.Close
                  render={<Button size="sm" />}
                  onClick={() => router.push("/vendors")}
                >
                  Add your first vendor
                </Dialog.Close>
              )}
            </div>
          </div>
        </Dialog.Popup>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
