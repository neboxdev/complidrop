"use client";

import { useEffect, useRef } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { CheckCircle2, Loader2, MailX } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { buttonVariants } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { hasSessionHint, useVerifyEmail } from "@/hooks/useAuth";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

/**
 * Email-verification landing page (#184). Opened from the link in the signup
 * email — possibly in a logged-out browser, so it stands alone. Reads the
 * `?token=`, redeems it once on mount, and renders the outcome. The endpoint is
 * idempotent (a re-clicked / already-redeemed link returns success), so a
 * StrictMode double-invoke or a refresh can't surface a false error — but we
 * still guard the fire with a ref to avoid a redundant second request.
 */
export function VerifyEmailClient() {
  const searchParams = useSearchParams();
  const token = searchParams.get("token")?.trim() ?? "";
  const verify = useVerifyEmail();
  // The link often opens in a logged-out browser. The non-httpOnly session-hint
  // cookie tells us — synchronously, no /me round-trip — whether this browser is
  // authenticated, so we can send them somewhere that works instead of bouncing
  // them to /login unexplained. (Using useMe() here would fetch /me and reset the
  // ME_KEY invalidation this page's success triggers.) (#316 FP-037)
  const loggedIn = hasSessionHint();
  const firedRef = useRef(false);

  useEffect(() => {
    if (firedRef.current || !token) return;
    firedRef.current = true;
    verify.mutate({ token });
  }, [token, verify]);

  if (!token) {
    return (
      <VerifyCard
        icon={<MailX className="mx-auto h-10 w-10 text-rose-500" />}
        title="Invalid verification link"
        body="This link looks incomplete. Open the most recent verification email, or resend a new one from your dashboard."
        action={<ContinueCta loggedIn={loggedIn} resend />}
      />
    );
  }

  if (verify.isError) {
    return (
      <VerifyCard
        icon={<MailX className="mx-auto h-10 w-10 text-rose-500" />}
        title="Couldn't confirm your email"
        // Server message first (e.g. "link has expired"), else the jargon-free
        // fallback — never raw HTTP status text (frontend error-message policy).
        body={verify.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}
        action={<ContinueCta loggedIn={loggedIn} resend />}
      />
    );
  }

  if (verify.isSuccess) {
    return (
      <VerifyCard
        icon={<CheckCircle2 className="mx-auto h-10 w-10 text-emerald-500" />}
        title="Email confirmed"
        body={verify.data?.message?.trim() || "Your email is confirmed — reminders will reach you."}
        action={<ContinueCta loggedIn={loggedIn} primary />}
      />
    );
  }

  // idle (effect about to fire) or pending → verifying
  return (
    <VerifyCard
      icon={<Loader2 className="mx-auto h-10 w-10 animate-spin text-sky-500" />}
      title="Confirming your email…"
      body="One moment while we verify your link."
    />
  );
}

function VerifyCard({
  icon,
  title,
  body,
  action,
}: {
  icon: React.ReactNode;
  title: string;
  body: string;
  action?: React.ReactNode;
}) {
  return (
    <Card className="border-sky-100 shadow-lg">
      <CardContent className="space-y-4 p-8 text-center" role="status" aria-live="polite">
        {icon}
        <h1 className="text-xl font-semibold text-sky-900">{title}</h1>
        <p className="text-sm text-slate-500">{body}</p>
        {action}
      </CardContent>
    </Card>
  );
}

function ContinueCta({
  loggedIn,
  primary = false,
  resend = false,
}: {
  loggedIn: boolean;
  primary?: boolean;
  resend?: boolean;
}) {
  // A logged-out visitor sent to /dashboard just bounces to /login unexplained
  // (#316 FP-037). Send them to sign in with honest copy; the resend action
  // lives on the dashboard, so logged-in users go there. base-ui's Button has no
  // `asChild`, so style the Link with buttonVariants — the link-as-button idiom.
  const href = loggedIn ? "/dashboard" : "/login";
  const label = loggedIn
    ? resend
      ? "Go to dashboard to resend"
      : "Continue to dashboard"
    : "Sign in to continue";
  return (
    <Link href={href} className={cn(buttonVariants({ variant: primary ? "default" : "outline" }), "mt-2")}>
      {label}
    </Link>
  );
}

// Server-rendered placeholder for the Suspense boundary in page.tsx.
export function VerifyEmailSkeleton() {
  return (
    <Card className="border-sky-100 shadow-lg">
      <CardContent className="space-y-4 p-8 text-center">
        <Loader2 className="mx-auto h-10 w-10 animate-spin text-sky-500" />
        <h1 className="text-xl font-semibold text-sky-900">Confirming your email…</h1>
      </CardContent>
    </Card>
  );
}
