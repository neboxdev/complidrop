"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PasswordInput } from "@/components/PasswordInput";
import { Card, CardContent } from "@/components/ui/card";
import {
  useChangePassword,
  useChangeEmail,
  useDeleteAccount,
} from "@/hooks/useAuth";
import { GENERIC_FALLBACK_MESSAGE, friendly } from "@/lib/api";

const API_BASE = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5292";

// Mirrors the backend IsStrongPassword check (≥12 chars, a letter, a digit).
const strongPassword = z
  .string()
  .min(12, "At least 12 characters")
  .regex(/[A-Za-z]/, "Include a letter")
  .regex(/[0-9]/, "Include a digit");

// ───────────────────────── Security ─────────────────────────

export function SecuritySection() {
  return (
    <Card>
      <CardContent className="p-6 space-y-6">
        <div>
          <h2 className="text-base font-semibold text-slate-800">Security</h2>
          <p className="text-sm text-slate-500">Change your password or the email you sign in with.</p>
        </div>
        <ChangePasswordForm />
        <hr className="border-slate-100" />
        <ChangeEmailForm />
      </CardContent>
    </Card>
  );
}

const changePasswordSchema = z
  .object({
    currentPassword: z.string().min(1, "Enter your current password"),
    newPassword: strongPassword,
    confirm: z.string(),
  })
  .refine((d) => d.newPassword === d.confirm, {
    message: "Passwords don't match",
    path: ["confirm"],
  });
type ChangePasswordForm = z.infer<typeof changePasswordSchema>;

function ChangePasswordForm() {
  const change = useChangePassword();
  const currentId = useId();
  const newId = useId();
  const confirmId = useId();
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<ChangePasswordForm>({ resolver: zodResolver(changePasswordSchema) });

  const onSubmit = async (values: ChangePasswordForm) => {
    try {
      await change.mutateAsync({
        currentPassword: values.currentPassword,
        newPassword: values.newPassword,
      });
      toast.success("Your password has been updated.");
      reset();
    } catch (err) {
      toast.error(friendly(err));
    }
  };

  return (
    <form onSubmit={handleSubmit(onSubmit)} className="space-y-3">
      <h3 className="text-sm font-medium text-slate-700">Change password</h3>
      <div>
        <label htmlFor={currentId} className="text-xs text-slate-500">Current password</label>
        <PasswordInput {...register("currentPassword")} id={currentId} autoComplete="current-password" className="mt-1" />
        {errors.currentPassword && <p className="text-xs text-red-600 mt-1">{errors.currentPassword.message}</p>}
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label htmlFor={newId} className="text-xs text-slate-500">New password</label>
          <PasswordInput {...register("newPassword")} id={newId} autoComplete="new-password" className="mt-1" />
          {errors.newPassword && <p className="text-xs text-red-600 mt-1">{errors.newPassword.message}</p>}
        </div>
        <div>
          <label htmlFor={confirmId} className="text-xs text-slate-500">Confirm new password</label>
          <PasswordInput {...register("confirm")} id={confirmId} autoComplete="new-password" className="mt-1" />
          {errors.confirm && <p className="text-xs text-red-600 mt-1">{errors.confirm.message}</p>}
        </div>
      </div>
      <Button type="submit" size="sm" disabled={change.isPending}>
        {change.isPending ? "Updating…" : "Update password"}
      </Button>
    </form>
  );
}

const changeEmailSchema = z.object({
  newEmail: z.string().email("Enter a valid email"),
  password: z.string().min(1, "Enter your password"),
});
type ChangeEmailForm = z.infer<typeof changeEmailSchema>;

function ChangeEmailForm() {
  const change = useChangeEmail();
  const emailId = useId();
  const pwId = useId();
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<ChangeEmailForm>({ resolver: zodResolver(changeEmailSchema) });

  const onSubmit = async (values: ChangeEmailForm) => {
    try {
      const res = await change.mutateAsync(values);
      // Server confirms the link was sent to the NEW address; surface its copy.
      toast.success(res?.message?.trim() || "Check your new email to confirm the change.");
      reset();
    } catch (err) {
      toast.error(friendly(err));
    }
  };

  return (
    <form onSubmit={handleSubmit(onSubmit)} className="space-y-3">
      <h3 className="text-sm font-medium text-slate-700">Change email</h3>
      <p className="text-xs text-slate-500">
        We&apos;ll send a confirmation link to the new address — your email changes only once you click it.
      </p>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label htmlFor={emailId} className="text-xs text-slate-500">New email</label>
          <Input {...register("newEmail")} id={emailId} type="email" autoComplete="email" className="mt-1" />
          {errors.newEmail && <p className="text-xs text-red-600 mt-1">{errors.newEmail.message}</p>}
        </div>
        <div>
          <label htmlFor={pwId} className="text-xs text-slate-500">Confirm with your password</label>
          <PasswordInput {...register("password")} id={pwId} autoComplete="current-password" className="mt-1" />
          {errors.password && <p className="text-xs text-red-600 mt-1">{errors.password.message}</p>}
        </div>
      </div>
      <Button type="submit" size="sm" variant="outline" disabled={change.isPending}>
        {change.isPending ? "Sending…" : "Send confirmation link"}
      </Button>
    </form>
  );
}

// ───────────────────────── Danger zone ─────────────────────────

export function DangerZone() {
  return (
    <Card className="border-rose-200">
      <CardContent className="p-6 space-y-6">
        <div>
          <h2 className="text-base font-semibold text-rose-700">Danger zone</h2>
          <p className="text-sm text-slate-500">Export your data, or permanently delete your account.</p>
        </div>
        <ExportDataButton />
        <hr className="border-slate-100" />
        <DeleteAccountForm />
      </CardContent>
    </Card>
  );
}

function ExportDataButton() {
  const [busy, setBusy] = useState(false);

  const onExport = async () => {
    setBusy(true);
    try {
      // Bare fetch (not the api.* client) — the response is a downloadable file,
      // not a JSON envelope. We own the jargon-free error discipline here (#77):
      // both branches collapse to GENERIC_FALLBACK_MESSAGE, never a raw status.
      let res = await fetch(`${API_BASE}/api/auth/account/export`, { credentials: "include" }).catch(() => null);
      // Mirror the api client's one-shot 401→refresh→retry so an expired
      // cd_session (but still-valid cd_refresh) doesn't dead-end the download.
      if (res?.status === 401) {
        const refreshed = await fetch(`${API_BASE}/api/auth/refresh`, {
          method: "POST",
          credentials: "include",
        }).catch(() => null);
        if (refreshed?.ok) {
          res = await fetch(`${API_BASE}/api/auth/account/export`, { credentials: "include" }).catch(() => null);
        }
      }
      if (!res || !res.ok) throw new Error(GENERIC_FALLBACK_MESSAGE);
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = "complidrop-account-export.json";
      link.click();
      URL.revokeObjectURL(url);
      toast.success("Download started");
    } catch (err) {
      toast.error(friendly(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-medium text-slate-700">Export your data</h3>
      <p className="text-xs text-slate-500">
        Download a JSON copy of your account, organization, vendors, documents, and reminders.
      </p>
      <Button type="button" size="sm" variant="outline" onClick={onExport} disabled={busy}>
        {busy ? "Preparing…" : "Export my data"}
      </Button>
    </div>
  );
}

const deleteSchema = z.object({ password: z.string().min(1, "Enter your password to confirm") });
type DeleteForm = z.infer<typeof deleteSchema>;

function DeleteAccountForm() {
  const router = useRouter();
  const del = useDeleteAccount();
  const [confirming, setConfirming] = useState(false);
  const pwId = useId();
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<DeleteForm>({ resolver: zodResolver(deleteSchema) });

  const onSubmit = async (values: DeleteForm) => {
    try {
      await del.mutateAsync(values);
      toast.success("Your account has been deleted.");
      router.push("/login");
    } catch (err) {
      toast.error(friendly(err));
    }
  };

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-medium text-slate-700">Delete account</h3>
      <p className="text-xs text-slate-500">
        Permanently deletes your account and organization data. This can&apos;t be undone. If you
        have a paid plan, it will be canceled — no new charges will start.
      </p>
      {!confirming ? (
        <Button type="button" size="sm" variant="destructive" onClick={() => setConfirming(true)}>
          Delete my account
        </Button>
      ) : (
        <form onSubmit={handleSubmit(onSubmit)} className="space-y-3 rounded-md border border-rose-200 bg-rose-50/50 p-3">
          <div>
            <label htmlFor={pwId} className="text-xs text-slate-600">Enter your password to confirm</label>
            <PasswordInput {...register("password")} id={pwId} autoComplete="current-password" className="mt-1" />
            {errors.password && <p className="text-xs text-red-600 mt-1">{errors.password.message}</p>}
          </div>
          <div className="flex gap-2">
            <Button type="submit" size="sm" variant="destructive" disabled={del.isPending}>
              {del.isPending ? "Deleting…" : "Permanently delete"}
            </Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setConfirming(false)} disabled={del.isPending}>
              Cancel
            </Button>
          </div>
        </form>
      )}
    </div>
  );
}
