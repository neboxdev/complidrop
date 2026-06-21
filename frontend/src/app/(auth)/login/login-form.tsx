"use client";

import { useId, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PasswordInput } from "@/components/PasswordInput";
import { Card, CardContent } from "@/components/ui/card";
import { useLogin } from "@/hooks/useAuth";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

const schema = z.object({
  email: z.string().email("Enter a valid email"),
  password: z.string().min(1, "Password is required"),
});

type LoginForm = z.infer<typeof schema>;

export default function LoginForm() {
  const router = useRouter();
  const login = useLogin();
  // React Hook Form's `register("name")` does NOT auto-emit an id, so
  // we generate one per field with `useId()` and thread it into both
  // the label's `htmlFor` and the input's `id` prop. Wires screen-
  // reader announcements and unlocks RTL's `getByLabelText` for tests.
  // (#76)
  const emailId = useId();
  const passwordId = useId();
  const emailErrId = useId();
  const passwordErrId = useId();
  const serverErrId = useId();
  const [serverError, setServerError] = useState<string | null>(null);
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<LoginForm>({ resolver: zodResolver(schema), mode: "onTouched" });

  const onSubmit = async (values: LoginForm) => {
    setServerError(null);
    try {
      await login.mutateAsync(values);
      toast.success("Welcome back!");
      router.push("/dashboard");
    } catch (err) {
      // Wrong password, the lockout message (which carries the ONLY instructions
      // for getting back in — reset your password), and 429 rate-limits must
      // persist on-screen, not evaporate in a 4-second toast. (#316 FP-033)
      setServerError(err instanceof Error && err.message ? err.message : GENERIC_FALLBACK_MESSAGE);
    }
  };

  return (
    <Card className="shadow-lg border-sky-100">
      <CardContent className="p-8 space-y-6">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">Welcome back</h1>
          <p className="text-sm text-slate-500">Sign in to your CompliDrop workspace.</p>
        </div>

        <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
          <div>
            <label htmlFor={emailId} className="text-sm font-medium text-slate-700">Email</label>
            <Input
              {...register("email")}
              id={emailId}
              type="email"
              autoComplete="email"
              className="mt-1"
              aria-invalid={errors.email ? true : undefined}
              aria-describedby={errors.email ? emailErrId : undefined}
            />
            {errors.email && <p id={emailErrId} className="text-xs text-red-600 mt-1">{errors.email.message}</p>}
          </div>
          <div>
            <div className="flex items-center justify-between">
              <label htmlFor={passwordId} className="text-sm font-medium text-slate-700">Password</label>
              {/* The lockout escape path (#183): a locked-out user resets to
                  regain access immediately. */}
              <Link href="/forgot-password" className="text-xs text-sky-700 hover:underline">
                Forgot your password?
              </Link>
            </div>
            <PasswordInput
              {...register("password")}
              id={passwordId}
              autoComplete="current-password"
              className="mt-1"
              aria-invalid={errors.password ? true : undefined}
              aria-describedby={errors.password ? passwordErrId : undefined}
            />
            {errors.password && <p id={passwordErrId} className="text-xs text-red-600 mt-1">{errors.password.message}</p>}
          </div>
          {serverError && (
            <p id={serverErrId} role="alert" className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
              {serverError}
            </p>
          )}
          <Button type="submit" className="w-full" disabled={login.isPending}>
            {login.isPending ? "Signing in…" : "Sign in"}
          </Button>
        </form>

        <p className="text-sm text-center text-slate-500">
          No account?{" "}
          <Link href="/register" className="text-sky-700 hover:underline">
            Create one
          </Link>
        </p>
      </CardContent>
    </Card>
  );
}
