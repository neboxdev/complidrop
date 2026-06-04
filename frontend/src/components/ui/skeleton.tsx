import { cn } from "@/lib/utils";

/**
 * A pulsing placeholder block. Use it to reserve the loaded content's footprint
 * while data is in flight — a shaped skeleton kills the layout shift (CLS) a bare
 * "Loading…" line causes when it's swapped for a tall table/list. `motion-safe`
 * so reduced-motion users get a static block (consistent with #189). (#197)
 */
function Skeleton({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      className={cn("rounded-md bg-slate-200/70 motion-safe:animate-pulse", className)}
      {...props}
    />
  );
}

export { Skeleton };
