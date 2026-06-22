import type { Metadata } from "next";
import { Suspense } from "react";
import LoginForm from "./login-form";

// Per-page title (the form is a client component and can't export metadata; the
// server wrapper carries it — same split as register/page.tsx). (#316 FP-013)
export const metadata: Metadata = { title: "Sign in" };

export default function LoginPage() {
  // LoginForm reads `useSearchParams()` (the #318 FP-045 expiry handoff), which
  // opts its subtree into client-side rendering — Next requires a Suspense
  // boundary around it so the rest of the route can still be prerendered.
  return (
    <Suspense>
      <LoginForm />
    </Suspense>
  );
}
