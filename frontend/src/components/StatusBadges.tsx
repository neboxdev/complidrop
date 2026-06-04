"use client";

/**
 * Compliance + extraction status badges with a LEADING ICON, so the state is
 * signalled by shape (icon) and text — not color alone. This both satisfies the
 * "color is never the sole signal" AC and fixes the detail page's old collapse
 * where Expired / ExpiringSoon / Pending all rendered as one slate pill: the
 * hue + icon now come from a single map shared by the list AND the detail. (#189)
 *
 * Extra props (e.g. `data-testid` on the detail page) flow through to the Badge.
 * The icon is aria-hidden — the badge's text already carries the meaning for AT.
 */
import type * as React from "react";
import { Badge } from "@/components/ui/badge";
import { complianceStatusLabel, extractionStatusLabel } from "@/lib/display-labels";
import { cn } from "@/lib/utils";
import {
  Check,
  AlertTriangle,
  Clock,
  XCircle,
  CircleDashed,
  RefreshCw,
  FileX,
  Hourglass,
  type LucideIcon,
} from "lucide-react";

type Variant = { hue: string; Icon: LucideIcon };

const COMPLIANCE: Record<string, Variant> = {
  Compliant: { hue: "bg-emerald-100 text-emerald-700", Icon: Check },
  NonCompliant: { hue: "bg-rose-100 text-rose-700", Icon: AlertTriangle },
  ExpiringSoon: { hue: "bg-amber-100 text-amber-700", Icon: Clock },
  Expired: { hue: "bg-rose-100 text-rose-700", Icon: XCircle },
  Pending: { hue: "bg-slate-100 text-slate-700", Icon: CircleDashed },
};

const EXTRACTION: Record<string, Variant> = {
  Pending: { hue: "bg-slate-100 text-slate-700", Icon: Hourglass },
  Processing: { hue: "bg-sky-100 text-sky-700 motion-safe:animate-pulse", Icon: RefreshCw },
  Completed: { hue: "bg-emerald-100 text-emerald-700", Icon: Check },
  ManualRequired: { hue: "bg-amber-100 text-amber-700", Icon: AlertTriangle },
  Failed: { hue: "bg-rose-100 text-rose-700", Icon: FileX },
};

export function ComplianceBadge({
  status,
  className,
  ...rest
}: { status: string } & React.ComponentProps<typeof Badge>) {
  const v = COMPLIANCE[status] ?? COMPLIANCE.Pending;
  return (
    <Badge className={cn("inline-flex items-center gap-1 border-transparent", v.hue, className)} {...rest}>
      <v.Icon className="h-3 w-3 shrink-0" aria-hidden />
      {complianceStatusLabel(status)}
    </Badge>
  );
}

export function ExtractionBadge({
  status,
  className,
  ...rest
}: { status: string } & React.ComponentProps<typeof Badge>) {
  const v = EXTRACTION[status] ?? EXTRACTION.Pending;
  return (
    <Badge className={cn("inline-flex items-center gap-1 border-transparent font-medium", v.hue, className)} {...rest}>
      <v.Icon className="h-3 w-3 shrink-0" aria-hidden />
      {extractionStatusLabel(status)}
    </Badge>
  );
}
