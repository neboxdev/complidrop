import { Suspense } from "react";
import { ResetPasswordClient, ResetPasswordSkeleton } from "./reset-password-client";

// The client reads `?token=` via useSearchParams(), which Next.js requires to
// live inside a <Suspense> boundary so the rest of the auth route can prerender
// (same pattern as register/verify-email, #31).
export default function ResetPasswordPage() {
  return (
    <Suspense fallback={<ResetPasswordSkeleton />}>
      <ResetPasswordClient />
    </Suspense>
  );
}
