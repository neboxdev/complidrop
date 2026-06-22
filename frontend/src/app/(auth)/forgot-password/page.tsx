import type { Metadata } from "next";
import ForgotPasswordForm from "./forgot-password-form";

// Per-page title (the form is a client component and can't export metadata; the
// server wrapper carries it — same split as register/page.tsx). (#316 FP-013)
export const metadata: Metadata = { title: "Reset your password" };

export default function ForgotPasswordPage() {
  return <ForgotPasswordForm />;
}
