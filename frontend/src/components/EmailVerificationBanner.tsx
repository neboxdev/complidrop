"use client";

import Link from "next/link";
import { MailWarning } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { useResendVerification } from "@/hooks/useAuth";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

/**
 * Persistent, unmissable dashboard banner shown until the user confirms their
 * signup email (#184). Reminders + audit emails go to that address, so an
 * unverified (possibly typo'd) email silently dead-letters everything — the
 * product's core value. We soft-gate (never block the app), just keep this
 * call-to-action visible with a one-tap Resend.
 *
 * `role="region"` + `aria-label` (not `role="alert"`): the banner is
 * persistent context, not a transient interruption — it shouldn't grab screen-
 * reader focus on every page mount, but should be reachable as a landmark.
 *
 * Error/success copy follows the frontend error-message policy: server message
 * when present, else GENERIC_FALLBACK_MESSAGE — never raw HTTP jargon.
 */
export function EmailVerificationBanner({ email }: { email: string }) {
  const resend = useResendVerification();

  const onResend = () =>
    resend.mutate(undefined, {
      onSuccess: (res) => toast.success(res?.message?.trim() || "Verification email sent."),
      onError: (err) =>
        toast.error(err instanceof Error && err.message ? err.message : GENERIC_FALLBACK_MESSAGE),
    });

  return (
    <div
      role="region"
      aria-label="Confirm your email"
      className="flex flex-col gap-2 border-b border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 sm:flex-row sm:items-center sm:justify-between"
    >
      <p className="flex items-start gap-2">
        <MailWarning className="mt-0.5 h-4 w-4 shrink-0 text-amber-600" />
        <span>
          Confirm your email <strong className="font-medium break-all">{email}</strong> so your
          compliance reminders actually reach you.{" "}
          {/* Escape hatch (#318 FP-049): a typo'd address can't be fixed by resending to
              it — Settings is where the change-email flow lives. */}
          <Link href="/settings" className="font-medium underline hover:text-amber-950">
            Not your email?
          </Link>
        </span>
      </p>
      <Button
        variant="outline"
        size="sm"
        onClick={onResend}
        disabled={resend.isPending}
        className="shrink-0 self-start border-amber-300 bg-white text-amber-900 hover:bg-amber-100 sm:self-auto"
      >
        {resend.isPending ? "Sending…" : "Resend email"}
      </Button>
    </div>
  );
}
