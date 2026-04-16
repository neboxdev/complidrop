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
import { useLogin } from "@/hooks/useAuth";

const schema = z.object({
  email: z.string().email("Enter a valid email"),
  password: z.string().min(1, "Password is required"),
});

type LoginForm = z.infer<typeof schema>;

export default function LoginPage() {
  const router = useRouter();
  const login = useLogin();
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<LoginForm>({ resolver: zodResolver(schema) });

  const onSubmit = async (values: LoginForm) => {
    try {
      await login.mutateAsync(values);
      toast.success("Welcome back!");
      router.push("/dashboard");
    } catch (err) {
      const message = err instanceof Error ? err.message : "Sign in failed.";
      toast.error(message);
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
            <label className="text-sm font-medium text-slate-700">Email</label>
            <Input {...register("email")} type="email" autoComplete="email" className="mt-1" />
            {errors.email && <p className="text-xs text-red-600 mt-1">{errors.email.message}</p>}
          </div>
          <div>
            <label className="text-sm font-medium text-slate-700">Password</label>
            <Input {...register("password")} type="password" autoComplete="current-password" className="mt-1" />
            {errors.password && <p className="text-xs text-red-600 mt-1">{errors.password.message}</p>}
          </div>
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
