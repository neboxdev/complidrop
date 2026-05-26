"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
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

// The three pricing CTAs on the landing page link to `/register?plan=free|pro|annual`
// (see src/app/page.tsx). The register page reads that param so the pricing-screen
// choice is visibly honored — without this the param is silently dropped (#31).
//
// Billing/Stripe enforcement of the chosen plan is a separate ticket (#31 Non-goals);
// today the value only drives copy on the signup screen. The backend `RegisterRequest`
// DTO does not accept `plan`, so we deliberately do NOT send it to /api/auth/register.
const KNOWN_PLANS = ["free", "pro", "annual"] as const;
type Plan = (typeof KNOWN_PLANS)[number];

function parsePlan(raw: string | null | undefined): Plan {
  return (KNOWN_PLANS as readonly string[]).includes(raw ?? "")
    ? (raw as Plan)
    : "free";
}

const PLAN_COPY: Record<
  Plan,
  { heading: string; subtitle: string; banner: string | null }
> = {
  free: {
    heading: "Start dropping docs",
    subtitle: "Free forever for 5 documents. No credit card.",
    banner: null,
  },
  pro: {
    heading: "Start your Pro account",
    subtitle: "Unlimited documents, vendor portal, multi-channel reminders.",
    banner: "You selected the Pro plan — $49/month. Cancel anytime.",
  },
  annual: {
    heading: "Start your Annual account",
    subtitle: "Unlimited documents, vendor portal, multi-channel reminders.",
    banner:
      "You selected the Annual plan — $39/month, billed $468/year. Save $120.",
  },
};

export default function RegisterForm() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const plan = parsePlan(searchParams.get("plan"));
  const copy = PLAN_COPY[plan];

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
    <Card className="shadow-lg border-sky-100" data-plan={plan}>
      <CardContent className="p-8 space-y-6">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">{copy.heading}</h1>
          <p className="text-sm text-slate-500">{copy.subtitle}</p>
        </div>

        {copy.banner && (
          <div
            role="status"
            className="flex items-center justify-between gap-3 rounded-lg border border-sky-100 bg-sky-50 px-4 py-3 text-sm text-sky-900"
          >
            <span>{copy.banner}</span>
            <Link
              href="/#pricing"
              className="shrink-0 text-sky-700 hover:underline"
            >
              Change
            </Link>
          </div>
        )}

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

// Server-rendered placeholder for the Suspense boundary in page.tsx. The form
// itself depends on useSearchParams(), which forces client-side rendering for
// everything inside the boundary; this fallback is what ships in the initial
// HTML before hydration. Match the real card's shape so layout doesn't jump.
export function RegisterFormSkeleton() {
  return (
    <Card className="shadow-lg border-sky-100">
      <CardContent className="p-8 space-y-6">
        <div>
          <div className="h-7 w-56 rounded bg-slate-100" />
          <div className="mt-2 h-4 w-72 rounded bg-slate-100" />
        </div>
        <div className="h-40 w-full rounded bg-slate-50" />
      </CardContent>
    </Card>
  );
}
