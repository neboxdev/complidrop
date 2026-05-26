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
// Exported so the landing-page test (src/app/page.test.tsx) can iterate the
// same set to assert every plan has a corresponding /register?plan=X CTA —
// pinning the round-trip contract between the pricing cards and this page.
export const KNOWN_PLANS = ["free", "pro", "annual"] as const;
type Plan = (typeof KNOWN_PLANS)[number];

// Tolerant parse: marketing emails and copy/pasted links occasionally arrive
// with mixed case (?plan=Annual) or trailing spaces. Lowercasing + trimming
// before the allowlist check keeps the user's choice instead of silently
// reverting to free. The allowlist itself remains the security boundary —
// anything outside { free, pro, annual } still falls back to "free".
function parsePlan(raw: string | null | undefined): Plan {
  const value = (raw ?? "").trim().toLowerCase();
  return (KNOWN_PLANS as readonly string[]).includes(value)
    ? (value as Plan)
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
    <Card className="shadow-lg border-sky-100">
      <CardContent className="p-8 space-y-6">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">{copy.heading}</h1>
          <p className="text-sm text-slate-500">{copy.subtitle}</p>
        </div>

        {copy.banner && (
          // Static page content rendered once from the URL; deliberately NOT
          // role="status" / aria-live (architecture-review #31) so screen
          // readers don't re-announce on every render. The banner is selected
          // in tests by its leading copy ("You selected the …") which appears
          // nowhere else in the form — accessible-text selectors are preferred
          // where they disambiguate naturally; data-testid is reserved for
          // surfaces that are ambiguous-by-design (see status-badge testids
          // added in #92 and the CLAUDE.md "Frontend testid policy" note).
          <div className="flex items-center justify-between gap-3 rounded-lg border border-sky-100 bg-sky-50 px-4 py-3 text-sm text-sky-900">
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
// HTML before hydration. Shape mirrors the real form (heading + 4 input rows
// + button + footer) so layout doesn't jump on hydration. aria-hidden because
// the live form will replace it within hydration — no need to announce twice.
// Lift `Row` to module scope so it isn't re-created on every render of
// `RegisterFormSkeleton`. The `react-hooks/static-components` rule fails CI
// on the prior inline form. Behavior is unchanged — `Row` is a pure
// stateless component.
function SkeletonRow() {
  return (
    <div>
      <div className="h-4 w-24 rounded bg-slate-100" />
      <div className="mt-2 h-10 w-full rounded bg-slate-50" />
    </div>
  );
}

export function RegisterFormSkeleton() {
  return (
    <Card className="shadow-lg border-sky-100" aria-hidden="true">
      <CardContent className="p-8 space-y-6">
        <div>
          <div className="h-7 w-56 rounded bg-slate-100" />
          <div className="mt-2 h-4 w-72 rounded bg-slate-100" />
        </div>
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-3">
            <SkeletonRow />
            <SkeletonRow />
          </div>
          <SkeletonRow />
          <SkeletonRow />
          <div className="grid grid-cols-2 gap-3">
            <SkeletonRow />
            <SkeletonRow />
          </div>
          <div className="h-10 w-full rounded bg-slate-100" />
        </div>
        <div className="mx-auto h-4 w-48 rounded bg-slate-50" />
      </CardContent>
    </Card>
  );
}
