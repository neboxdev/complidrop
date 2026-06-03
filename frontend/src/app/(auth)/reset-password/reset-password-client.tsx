"use client";

import { useId, useState } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardContent } from "@/components/ui/card";
import { useResetPassword } from "@/hooks/useAuth";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

// Mirrors the backend IsStrongPassword check (≥12 chars, a letter, a digit) so
// the user gets inline feedback before the round-trip.
const schema = z
  .object({
    password: z
      .string()
      .min(12, "At least 12 characters")
      .regex(/[A-Za-z]/, "Include a letter")
      .regex(/[0-9]/, "Include a digit"),
    confirm: z.string(),
  })
  .refine((d) => d.password === d.confirm, {
    message: "Passwords don't match",
    path: ["confirm"],
  });
type ResetForm = z.infer<typeof schema>;

export function ResetPasswordClient() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const token = searchParams.get("token")?.trim() ?? "";
  const reset = useResetPassword();
  const passwordId = useId();
  const confirmId = useId();
  const [done, setDone] = useState(false);
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<ResetForm>({ resolver: zodResolver(schema) });

  const onSubmit = async (values: ResetForm) => {
    try {
      await reset.mutateAsync({ token, newPassword: values.password });
      setDone(true);
      // Brief success state, then send them to sign in with the new password.
      setTimeout(() => router.push("/login"), 1200);
    } catch (err) {
      toast.error(err instanceof Error && err.message ? err.message : GENERIC_FALLBACK_MESSAGE);
    }
  };

  if (!token) {
    return (
      <Card className="border-sky-100 shadow-lg">
        <CardContent className="space-y-3 p-8 text-center">
          <h1 className="text-xl font-semibold text-sky-900">Invalid reset link</h1>
          <p className="text-sm text-slate-500">
            This link is missing its token. Request a fresh one from the sign-in page.
          </p>
          <Link href="/forgot-password" className="inline-block text-sm text-sky-700 hover:underline">
            Request a new link
          </Link>
        </CardContent>
      </Card>
    );
  }

  if (done) {
    return (
      <Card className="border-sky-100 shadow-lg">
        <CardContent className="space-y-3 p-8 text-center">
          <h1 className="text-xl font-semibold text-sky-900">Password reset</h1>
          <p className="text-sm text-slate-500">Taking you to sign in…</p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card className="shadow-lg border-sky-100">
      <CardContent className="p-8 space-y-6">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">Choose a new password</h1>
          <p className="text-sm text-slate-500">Set the password you&apos;ll use to sign in.</p>
        </div>

        <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
          <div>
            <label htmlFor={passwordId} className="text-sm font-medium text-slate-700">New password</label>
            <Input {...register("password")} id={passwordId} type="password" autoComplete="new-password" className="mt-1" />
            {errors.password && <p className="text-xs text-red-600 mt-1">{errors.password.message}</p>}
          </div>
          <div>
            <label htmlFor={confirmId} className="text-sm font-medium text-slate-700">Confirm new password</label>
            <Input {...register("confirm")} id={confirmId} type="password" autoComplete="new-password" className="mt-1" />
            {errors.confirm && <p className="text-xs text-red-600 mt-1">{errors.confirm.message}</p>}
          </div>
          <Button type="submit" className="w-full" disabled={reset.isPending}>
            {reset.isPending ? "Resetting…" : "Reset password"}
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

export function ResetPasswordSkeleton() {
  return (
    <Card className="border-sky-100 shadow-lg">
      <CardContent className="space-y-3 p-8 text-center">
        <h1 className="text-xl font-semibold text-sky-900">Choose a new password</h1>
        <p className="text-sm text-slate-500">Loading…</p>
      </CardContent>
    </Card>
  );
}
