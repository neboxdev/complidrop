import { Suspense } from "react";
import RegisterForm, { RegisterFormSkeleton } from "./register-form";

// The form reads `?plan=` via useSearchParams() (#31), which Next.js requires
// to live inside a <Suspense> boundary so the rest of the auth route can
// prerender as static HTML. Production builds fail with "Missing Suspense
// boundary with useSearchParams" without this wrapper.
//
// See node_modules/next/dist/docs/01-app/03-api-reference/04-functions/use-search-params.md
// section "Prerendering" for the canonical pattern.
export default function RegisterPage() {
  return (
    <Suspense fallback={<RegisterFormSkeleton />}>
      <RegisterForm />
    </Suspense>
  );
}
