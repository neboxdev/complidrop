"use client";

import { useId } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Check, Circle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PasswordInput } from "@/components/PasswordInput";
import { Card, CardContent } from "@/components/ui/card";
import { useRegister } from "@/hooks/useAuth";
import { cn } from "@/lib/utils";

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
//
// Plan ids + display copy live in src/lib/plans.ts — the single source of truth
// across the landing page, this form, the opengraph image, and the settings
// page (#71). The landing-page test imports `KNOWN_PLAN_IDS` directly from
// `@/lib/plans` (#71 followup — the cross-route re-export shim was removed).
import { parsePlanId, PLANS, type PlanId } from "@/lib/plans";

// Per-plan banner heading/subtitle stays here; the banner BODY copy
// (with the dollar values) lives in PLANS so it can't drift from the
// landing page's pricing cards.
const PLAN_HEADINGS: Record<
  PlanId,
  { heading: string; subtitle: string }
> = {
  free: {
    heading: "Start dropping docs",
    subtitle: "Free forever for 5 documents. No credit card.",
  },
  pro: {
    heading: "Start your Pro account",
    subtitle: "Unlimited documents, a no-login vendor upload link, and automatic email reminders.",
  },
  annual: {
    heading: "Start your Annual account",
    subtitle: "Unlimited documents, a no-login vendor upload link, and automatic email reminders.",
  },
};

// Live password-criteria checklist — each rule turns green as the user satisfies
// it, so the 12-char minimum reads as guidance instead of a post-submit rejection.
// Rules mirror the zod schema above. Module scope per the static-components rule (#73). (#195)
const PASSWORD_RULES: ReadonlyArray<{ label: string; test: (v: string) => boolean }> = [
  { label: "At least 12 characters", test: (v) => v.length >= 12 },
  { label: "A letter", test: (v) => /[A-Za-z]/.test(v) },
  { label: "A number", test: (v) => /[0-9]/.test(v) },
];

function PasswordChecklist({ value, id }: { value: string; id: string }) {
  return (
    <ul id={id} className="mt-2 space-y-1">
      {PASSWORD_RULES.map((rule) => {
        const ok = rule.test(value);
        return (
          <li
            key={rule.label}
            className={cn(
              "flex items-center gap-1.5 text-xs",
              ok ? "text-emerald-700" : "text-slate-500",
            )}
          >
            {ok ? (
              <Check className="h-3.5 w-3.5 shrink-0" aria-hidden />
            ) : (
              <Circle className="h-3 w-3 shrink-0" aria-hidden />
            )}
            {rule.label}
          </li>
        );
      })}
    </ul>
  );
}

export default function RegisterForm() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const plan = parsePlanId(searchParams.get("plan"));
  const copy = {
    ...PLAN_HEADINGS[plan],
    banner: PLANS[plan].bannerCopy,
  };

  const register = useRegister();
  // useId() per field for label/input association — a11y + unlocks
  // RTL's getByLabelText for tests (#76). RHF's register("name") does
  // not auto-emit an id, so we thread it through explicitly.
  const fullNameId = useId();
  const companyNameId = useId();
  const emailId = useId();
  const passwordId = useId();
  const passwordHintId = useId();
  const industryId = useId();
  const companySizeId = useId();
  // One error-message id per field so the input can point at it via
  // aria-describedby — without this a screen reader never hears the error (#189).
  const fullNameErrId = useId();
  const companyNameErrId = useId();
  const emailErrId = useId();
  const passwordErrId = useId();
  const {
    register: r,
    handleSubmit,
    control,
    formState: { errors },
  } = useForm<RegisterForm>({
    resolver: zodResolver(schema),
    // Validate (and surface errors) after the field is touched, not only on
    // submit, so SR users hear the problem as they move through the form. RHF's
    // default shouldFocusError moves focus to the first invalid field on submit.
    mode: "onTouched",
  });
  // Subscription-based watch (compiler-friendly, unlike calling watch() inline)
  // drives the live password checklist. (#195)
  const passwordValue = useWatch({ control, name: "password" }) ?? "";

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
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label htmlFor={fullNameId} className="text-sm font-medium text-slate-700">Full name</label>
              <Input
                {...r("fullName")}
                id={fullNameId}
                className="mt-1"
                aria-invalid={errors.fullName ? true : undefined}
                aria-describedby={errors.fullName ? fullNameErrId : undefined}
              />
              {errors.fullName && <p id={fullNameErrId} className="text-xs text-red-600 mt-1">{errors.fullName.message}</p>}
            </div>
            <div>
              <label htmlFor={companyNameId} className="text-sm font-medium text-slate-700">Company</label>
              <Input
                {...r("companyName")}
                id={companyNameId}
                className="mt-1"
                aria-invalid={errors.companyName ? true : undefined}
                aria-describedby={errors.companyName ? companyNameErrId : undefined}
              />
              {errors.companyName && <p id={companyNameErrId} className="text-xs text-red-600 mt-1">{errors.companyName.message}</p>}
            </div>
          </div>
          <div>
            <label htmlFor={emailId} className="text-sm font-medium text-slate-700">Work email</label>
            <Input
              {...r("email")}
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
            <label htmlFor={passwordId} className="text-sm font-medium text-slate-700">Password</label>
            <PasswordInput
              {...r("password")}
              id={passwordId}
              autoComplete="new-password"
              className="mt-1"
              aria-invalid={errors.password ? true : undefined}
              aria-describedby={`${errors.password ? `${passwordErrId} ` : ""}${passwordHintId}`}
            />
            {errors.password && <p id={passwordErrId} className="text-xs text-red-600 mt-1">{errors.password.message}</p>}
            <PasswordChecklist value={passwordValue} id={passwordHintId} />
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label htmlFor={industryId} className="text-sm font-medium text-slate-700">
                Industry <span className="font-normal text-slate-400">(optional)</span>
              </label>
              <Input {...r("industry")} id={industryId} placeholder="Construction, healthcare…" className="mt-1" />
            </div>
            <div>
              <label htmlFor={companySizeId} className="text-sm font-medium text-slate-700">
                Size <span className="font-normal text-slate-400">(optional)</span>
              </label>
              <Input {...r("companySize")} id={companySizeId} placeholder="10-30" className="mt-1" />
            </div>
          </div>
          <Button type="submit" className="w-full" disabled={register.isPending}>
            {register.isPending ? "Creating account…" : "Create my account"}
          </Button>
          {/* Repeat the risk-free reassurance right at the commit point (#195). */}
          <p className="text-center text-xs text-slate-500">
            Free for your first 5 documents. No credit card required.
          </p>
          {/* Affirmative assent so the Terms + Privacy Policy actually bind the
              user (clickwrap-style), with both links discoverable at signup. (#194) */}
          <p className="mt-3 text-center text-xs text-slate-500">
            By creating an account, you agree to our{" "}
            <Link href="/terms" className="text-sky-700 hover:underline">
              Terms of Service
            </Link>{" "}
            and{" "}
            <Link href="/privacy" className="text-sky-700 hover:underline">
              Privacy Policy
            </Link>
            .
          </p>
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
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <SkeletonRow />
            <SkeletonRow />
          </div>
          <SkeletonRow />
          <SkeletonRow />
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
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
