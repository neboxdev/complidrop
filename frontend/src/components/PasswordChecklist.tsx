import { Check, Circle } from "lucide-react";
import { cn } from "@/lib/utils";

// Live password-criteria checklist — each rule turns green as the user satisfies
// it, so the 12-char minimum reads as guidance instead of a post-submit rejection.
// Rules mirror the backend IsStrongPassword check (>=12 chars, a letter, a digit)
// and the zod schemas on the register + reset forms. Shared so the two forms can't
// drift (#316 FP-035). Module scope per the static-components rule (#73). (#195)
export const PASSWORD_RULES: ReadonlyArray<{ label: string; test: (v: string) => boolean }> = [
  { label: "At least 12 characters", test: (v) => v.length >= 12 },
  { label: "A letter", test: (v) => /[A-Za-z]/.test(v) },
  { label: "A number", test: (v) => /[0-9]/.test(v) },
];

export function PasswordChecklist({ value, id }: { value: string; id: string }) {
  return (
    <ul id={id} className="mt-2 space-y-1">
      {PASSWORD_RULES.map((rule) => {
        const ok = rule.test(value);
        return (
          <li
            key={rule.label}
            data-met={ok ? "true" : "false"}
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
