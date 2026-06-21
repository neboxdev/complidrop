"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { friendly } from "@/lib/api";
import { useDashboardStats } from "@/hooks/useDashboard";
import { useSeedSample, useClearSample } from "@/hooks/useSample";

const TRY_LABEL = "Try it with a sample certificate";

/**
 * "Try it with a sample certificate" CTA (#238). Seeds the demo, then deep-links to the new
 * sample document so the user lands on a live verdict in ~a minute. Disabled while in flight so a
 * double-click can't fire twice (the server is idempotent regardless).
 */
export function TrySampleButton({
  variant = "outline",
  size = "sm",
  className,
  label = TRY_LABEL,
  showIcon = true,
}: {
  variant?: "outline" | "link" | "default";
  size?: "sm" | "default";
  className?: string;
  label?: string;
  showIcon?: boolean;
}) {
  const router = useRouter();
  const seed = useSeedSample();
  return (
    <Button
      type="button"
      variant={variant}
      size={size}
      className={className}
      disabled={seed.isPending}
      onClick={() =>
        seed.mutate(undefined, {
          onSuccess: (res) => {
            toast.success("Sample certificate added — we're reading it now.");
            router.push(`/documents/${res.documentId}`);
          },
          onError: (err) => toast.error(friendly(err)),
        })
      }
    >
      {showIcon && <Sparkles aria-hidden="true" />}
      {seed.isPending ? "Setting up your sample…" : label}
    </Button>
  );
}

/**
 * "Clear sample data" — removes every sample artifact for the org (#238). `onCleared` lets a caller
 * navigate away from a now-deleted sample (the detail page redirects to /documents).
 */
export function ClearSampleButton({
  onCleared,
  className,
  label = "Clear sample data",
}: {
  onCleared?: () => void;
  className?: string;
  label?: string;
}) {
  const clear = useClearSample();
  return (
    <Button
      type="button"
      variant="outline"
      size="sm"
      className={className}
      disabled={clear.isPending}
      onClick={() =>
        clear.mutate(undefined, {
          onSuccess: () => {
            toast.success("Sample data cleared.");
            onCleared?.();
          },
          onError: (err) => toast.error(friendly(err)),
        })
      }
    >
      {clear.isPending ? "Clearing…" : label}
    </Button>
  );
}

/**
 * Dashboard banner shown while sample-demo data exists (#238): labels the org's data as a demo and
 * offers one-click "View sample" + "Clear sample data". Reads dashboard stats itself and renders
 * nothing when there's no sample, so the dashboard can mount it unconditionally.
 */
export function SampleDataBanner() {
  const stats = useDashboardStats();
  const s = stats.data;
  if (!s?.hasSampleData) return null;
  return (
    <div className="flex flex-col gap-3 rounded-lg border border-sky-200 bg-sky-50 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
      <p className="text-sm text-sky-900">
        <span className="font-semibold">You&apos;re exploring with sample data.</span>{" "}
        This is a demo certificate — clear it whenever you&apos;re ready to add your own.
      </p>
      <div className="flex shrink-0 items-center gap-3">
        {s.sampleDocumentId && (
          <Link
            href={`/documents/${s.sampleDocumentId}`}
            className="text-sm font-medium text-sky-700 hover:underline"
          >
            View sample
          </Link>
        )}
        <ClearSampleButton />
      </div>
    </div>
  );
}
