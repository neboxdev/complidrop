"use client";

/**
 * `Sheet` — a slide-in panel (drawer) built on the Base UI `Dialog` primitive.
 *
 * Used by the responsive app shell (mobile nav drawer, `side="left"`) and the
 * marketing header (mobile menu, `side="right"`). Building on `Dialog` gives us
 * focus-trapping, scroll-lock, Escape-to-close, outside-click dismissal, and the
 * required `aria-modal` wiring for free — a hand-rolled drawer would have to
 * re-implement all of that (and get it wrong). See #181.
 *
 * Consumers MUST render a `<SheetTitle>` inside `<SheetContent>` (visually
 * hidden is fine via `className="sr-only"`) — Base UI warns when a dialog has
 * no accessible name, and screen-reader users need one to know what the drawer
 * is. `<SheetClose>` should also be rendered inside so touch screen-reader users
 * have an explicit escape affordance (Base UI's guidance for modal popups).
 *
 * Enter/exit transitions are driven by Base UI's `data-[starting-style]` /
 * `data-[ending-style]` attributes (set on the popup/backdrop during the
 * mount/unmount transition window); `motion-reduce:` short-circuits the
 * animation for users who ask for reduced motion.
 */
import * as React from "react";
import { Dialog } from "@base-ui/react/dialog";

import { cn } from "@/lib/utils";

const Sheet = Dialog.Root;
const SheetTrigger = Dialog.Trigger;
const SheetClose = Dialog.Close;
const SheetTitle = Dialog.Title;
const SheetDescription = Dialog.Description;

function SheetContent({
  side = "left",
  className,
  children,
  ...props
}: React.ComponentProps<typeof Dialog.Popup> & {
  side?: "left" | "right";
}) {
  return (
    <Dialog.Portal>
      <Dialog.Backdrop
        className={cn(
          "fixed inset-0 z-50 bg-slate-900/50 transition-opacity duration-200",
          "data-[starting-style]:opacity-0 data-[ending-style]:opacity-0",
          "motion-reduce:transition-none",
        )}
      />
      <Dialog.Popup
        className={cn(
          "fixed top-0 z-50 flex h-dvh w-72 max-w-[85vw] flex-col overflow-y-auto bg-white shadow-xl outline-none",
          "transition-transform duration-200 ease-out motion-reduce:transition-none",
          side === "left" &&
            "left-0 data-[starting-style]:-translate-x-full data-[ending-style]:-translate-x-full",
          side === "right" &&
            "right-0 data-[starting-style]:translate-x-full data-[ending-style]:translate-x-full",
          className,
        )}
        {...props}
      >
        {children}
      </Dialog.Popup>
    </Dialog.Portal>
  );
}

export {
  Sheet,
  SheetTrigger,
  SheetClose,
  SheetContent,
  SheetTitle,
  SheetDescription,
};
