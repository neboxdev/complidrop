import Link from "next/link";
import { Badge } from "@/components/ui/badge";
import type { VendorCoverage } from "@/hooks/useVendors";

/**
 * Renders a vendor's coverage rollup (#319 FP-074) so the list + detail can answer
 * "who is NOT ok?" at a glance:
 *   - Covered      → green badge
 *   - ActionNeeded → rose "Action needed" (a required type's latest doc isn't compliant)
 *   - Missing      → rose "Missing: insurance, license" (a required type has no document)
 *   - NoRequirements → a "Set requirements" link (when `noRequirementsHref` is given) or muted text
 * The verdict is computed server-side; this is presentation only.
 */
export function VendorCoverageBadge({
  coverage,
  noRequirementsHref,
}: {
  coverage: VendorCoverage;
  /** When set, the no-requirements state links here (the vendor's own page) instead of muted text. */
  noRequirementsHref?: string;
}) {
  switch (coverage.status) {
    case "Covered":
      return <Badge className="bg-emerald-100 text-emerald-800 border-transparent">Covered</Badge>;
    case "ActionNeeded":
      return <Badge className="bg-rose-100 text-rose-700 border-transparent">Action needed</Badge>;
    case "Missing":
      return (
        <Badge className="bg-rose-100 text-rose-700 border-transparent">
          Missing: {coverage.missingTypes.join(", ")}
        </Badge>
      );
    default:
      return noRequirementsHref ? (
        <Link href={noRequirementsHref} className="text-xs text-sky-700 hover:underline">
          Set requirements
        </Link>
      ) : (
        <span className="text-xs text-slate-500">No requirements set</span>
      );
  }
}
