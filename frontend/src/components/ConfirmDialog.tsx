"use client";

/**
 * Accessible confirm dialog built on the Base UI `AlertDialog` primitive —
 * replaces native `confirm()` (which is unstyled, can't be themed, and on some
 * platforms isn't reliably reachable by assistive tech). Base UI gives us a
 * focus trap, labelling via the Title/Description, Escape-to-close, and — the
 * key bit native confirm() also did but a hand-rolled modal forgets — RETURNS
 * FOCUS to the trigger on close. (#189)
 *
 * Usage:
 *   <ConfirmDialog
 *     trigger={<Button aria-label="Remove file.pdf">…</Button>}
 *     title="Remove file.pdf?"
 *     confirmLabel="Remove"
 *     destructive
 *     onConfirm={() => del.mutate(id)}
 *   />
 */
import * as React from "react";
import { AlertDialog } from "@base-ui/react/alert-dialog";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

export function ConfirmDialog({
  trigger,
  title,
  description,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
  destructive = false,
  onConfirm,
}: {
  trigger: React.ReactNode;
  title: string;
  description?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
  onConfirm: () => void;
}) {
  return (
    <AlertDialog.Root>
      <AlertDialog.Trigger render={trigger as React.ReactElement} />
      <AlertDialog.Portal>
        <AlertDialog.Backdrop
          className={cn(
            "fixed inset-0 z-50 bg-slate-900/50 transition-opacity duration-150",
            "data-[starting-style]:opacity-0 data-[ending-style]:opacity-0 motion-reduce:transition-none",
          )}
        />
        <AlertDialog.Popup
          className={cn(
            "fixed left-1/2 top-1/2 z-50 w-[90vw] max-w-sm -translate-x-1/2 -translate-y-1/2 rounded-lg bg-white p-6 shadow-xl outline-none",
            "data-[starting-style]:scale-95 data-[starting-style]:opacity-0 data-[ending-style]:scale-95 data-[ending-style]:opacity-0",
            "transition-[transform,opacity] duration-150 motion-reduce:transition-none",
          )}
        >
          <AlertDialog.Title className="text-base font-semibold text-slate-900">
            {title}
          </AlertDialog.Title>
          {description && (
            <AlertDialog.Description className="mt-1.5 text-sm text-slate-600">
              {description}
            </AlertDialog.Description>
          )}
          <div className="mt-5 flex justify-end gap-2">
            <AlertDialog.Close render={<Button variant="ghost" size="sm" />}>
              {cancelLabel}
            </AlertDialog.Close>
            <AlertDialog.Close
              render={
                <Button
                  size="sm"
                  className={destructive ? "bg-rose-600 hover:bg-rose-700" : undefined}
                />
              }
              onClick={onConfirm}
            >
              {confirmLabel}
            </AlertDialog.Close>
          </div>
        </AlertDialog.Popup>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  );
}
