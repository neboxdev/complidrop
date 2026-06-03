import { Suspense } from "react";
import { VerifyEmailClient, VerifyEmailSkeleton } from "./verify-email-client";

// The client reads `?token=` via useSearchParams(), which Next.js requires to
// live inside a <Suspense> boundary so the rest of the auth route can prerender
// as static HTML (same pattern as register/page.tsx, #31). Production builds
// fail with "Missing Suspense boundary with useSearchParams" without this.
export default function VerifyEmailPage() {
  return (
    <Suspense fallback={<VerifyEmailSkeleton />}>
      <VerifyEmailClient />
    </Suspense>
  );
}
