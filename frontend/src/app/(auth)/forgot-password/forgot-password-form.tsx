"use client";

import { useId, useState } from "react";
import Link from "next/link";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardContent } from "@/components/ui/card";
import { useForgotPassword } from "@/hooks/useAuth";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

const schema = z.object({ email: z.string().email("Enter a valid email") });
type ForgotForm = z.infer<typeof schema>;

export default function ForgotPasswordForm() {
  const forgot = useForgotPassword();
  const emailId = useId();
  const errId = useId();
  const [sent, setSent] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<ForgotForm>({ resolver: zodResolver(schema) });

  // The server returns the same 200 whether or not the email exists, so a
  // successful request always shows the same neutral confirmation — never
  // revealing account existence. But a THROW here is a real failure (network,
  // rate-limit, 5xx): we must NOT flip to "Check your email", or the user trusts
  // an email that never sent. Show the failure instead. (#316 FP-031)
  const onSubmit = async (values: ForgotForm) => {
    setSubmitError(null);
    try {
      await forgot.mutateAsync(values);
      setSent(true);
    } catch (err) {
      setSubmitError(err instanceof Error && err.message ? err.message : GENERIC_FALLBACK_MESSAGE);
    }
  };

  if (sent) {
    return (
      <Card className="border-sky-100 shadow-lg">
        <CardContent className="space-y-3 p-8 text-center">
          <h1 className="text-xl font-semibold text-sky-900">Check your email</h1>
          <p className="text-sm text-slate-500">
            If that email is registered, we&apos;ve sent a link to reset your password. It expires in 45 minutes.
          </p>
          <Link href="/login" className="inline-block text-sm text-sky-700 hover:underline">
            Back to sign in
          </Link>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card className="shadow-lg border-sky-100">
      <CardContent className="p-8 space-y-6">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">Reset your password</h1>
          <p className="text-sm text-slate-500">
            Enter your email and we&apos;ll send you a link to set a new password.
          </p>
        </div>

        <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
          <div>
            <label htmlFor={emailId} className="text-sm font-medium text-slate-700">Email</label>
            <Input {...register("email")} id={emailId} type="email" autoComplete="email" className="mt-1" />
            {errors.email && <p className="text-xs text-red-600 mt-1">{errors.email.message}</p>}
          </div>
          {submitError && (
            <p id={errId} role="alert" className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
              {submitError}
            </p>
          )}
          <Button type="submit" className="w-full" disabled={forgot.isPending}>
            {forgot.isPending ? "Sending…" : "Send reset link"}
          </Button>
        </form>

        <p className="text-sm text-center text-slate-500">
          Remembered it?{" "}
          <Link href="/login" className="text-sky-700 hover:underline">Sign in</Link>
        </p>
      </CardContent>
    </Card>
  );
}
