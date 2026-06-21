"use client";

/**
 * Password field with an accessible show/hide toggle. The toggle is a real
 * <button> with `aria-pressed` reflecting visibility and an `aria-label` that
 * flips between "Show password" / "Hide password", so screen-reader users get
 * both the control's purpose and its current state. Forwards every prop
 * (`id`, `value`, `onChange`, `aria-describedby`, …) to the underlying Input,
 * so the #76 label-wiring + #189 error-describedby contracts still hold. (#189)
 */
import * as React from "react";
import { Eye, EyeOff } from "lucide-react";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";

export function PasswordInput({
  className,
  ...props
}: Omit<React.ComponentProps<typeof Input>, "type">) {
  const [visible, setVisible] = React.useState(false);
  return (
    <div className="relative">
      <Input
        {...props}
        type={visible ? "text" : "password"}
        className={cn("pr-10", className)}
      />
      <button
        type="button"
        aria-pressed={visible}
        aria-label={visible ? "Hide password" : "Show password"}
        onClick={() => setVisible((v) => !v)}
        className={cn(
          "absolute right-1.5 top-1/2 inline-flex h-8 w-8 -translate-y-1/2 items-center justify-center rounded text-slate-500",
          "hover:text-slate-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sky-600",
        )}
      >
        {visible ? <EyeOff className="h-4 w-4" aria-hidden /> : <Eye className="h-4 w-4" aria-hidden />}
      </button>
    </div>
  );
}
