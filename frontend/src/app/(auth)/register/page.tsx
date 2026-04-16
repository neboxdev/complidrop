"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardContent } from "@/components/ui/card";
import { useRegister } from "@/hooks/useAuth";

const schema = z.object({
  fullName: z.string().min(2, "Your full name is required"),
  companyName: z.string().min(2, "Company name is required"),
  email: z.string().email("Enter a valid email"),
  password: z
    .string()
    .min(12, "Password must be at least 12 characters")
    .regex(/[A-Za-z]/, "Password must include a letter")
    .regex(/[0-9]/, "Password must include a digit"),
  industry: z.string().optional(),
  companySize: z.string().optional(),
});

type RegisterForm = z.infer<typeof schema>;

export default function RegisterPage() {
  const router = useRouter();
  const register = useRegister();
  const {
    register: r,
    handleSubmit,
    formState: { errors },
  } = useForm<RegisterForm>({ resolver: zodResolver(schema) });

  const onSubmit = async (values: RegisterForm) => {
    try {
      const tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
      await register.mutateAsync({ ...values, timeZone: tz });
      toast.success("Account created. Welcome!");
      router.push("/dashboard");
    } catch (err) {
      const message = err instanceof Error ? err.message : "Sign up failed.";
      toast.error(message);
    }
  };

  return (
    <Card className="shadow-lg border-sky-100">
      <CardContent className="p-8 space-y-6">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">Start dropping docs</h1>
          <p className="text-sm text-slate-500">Free forever for 5 documents. No credit card.</p>
        </div>

        <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="text-sm font-medium text-slate-700">Full name</label>
              <Input {...r("fullName")} className="mt-1" />
              {errors.fullName && <p className="text-xs text-red-600 mt-1">{errors.fullName.message}</p>}
            </div>
            <div>
              <label className="text-sm font-medium text-slate-700">Company</label>
              <Input {...r("companyName")} className="mt-1" />
              {errors.companyName && <p className="text-xs text-red-600 mt-1">{errors.companyName.message}</p>}
            </div>
          </div>
          <div>
            <label className="text-sm font-medium text-slate-700">Work email</label>
            <Input {...r("email")} type="email" autoComplete="email" className="mt-1" />
            {errors.email && <p className="text-xs text-red-600 mt-1">{errors.email.message}</p>}
          </div>
          <div>
            <label className="text-sm font-medium text-slate-700">Password</label>
            <Input {...r("password")} type="password" autoComplete="new-password" className="mt-1" />
            {errors.password && <p className="text-xs text-red-600 mt-1">{errors.password.message}</p>}
            <p className="text-xs text-slate-500 mt-1">Min 12 chars, with a letter and a digit.</p>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="text-sm font-medium text-slate-700">Industry</label>
              <Input {...r("industry")} placeholder="Construction, healthcare…" className="mt-1" />
            </div>
            <div>
              <label className="text-sm font-medium text-slate-700">Size</label>
              <Input {...r("companySize")} placeholder="10-30" className="mt-1" />
            </div>
          </div>
          <Button type="submit" className="w-full" disabled={register.isPending}>
            {register.isPending ? "Creating account…" : "Create my account"}
          </Button>
        </form>

        <p className="text-sm text-center text-slate-500">
          Already have an account?{" "}
          <Link href="/login" className="text-sky-700 hover:underline">
            Sign in
          </Link>
        </p>
      </CardContent>
    </Card>
  );
}
