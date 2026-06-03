/**
 * Login page — full-stack component test against the harness (#35).
 *
 * Covers:
 *   1. RHF + Zod client validation (empty email, empty password).
 *   2. Happy path: POST /api/auth/login 200 → toast.success + router.push.
 *   3. Error copy: every server-side rejection (401 invalid creds, 423
 *      lockout, 500 generic) surfaces the human message — NEVER the raw
 *      code (auth.invalid_credentials / auth.locked / server.error).
 *   4. Loading state on the submit button reflects mutation pending.
 *
 * This file deliberately drives the FULL data path through MSW (not a
 * `vi.mock("@/hooks/useAuth")` shortcut) so a regression in any layer —
 * lib/api.ts envelope mapping, useLogin's mutation wiring, the toast
 * call — fails the test. The harness's `auth: null` seed keeps useMe()
 * silent on the auth layout so the form is the only thing under test.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
import LoginPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  toastSuccess,
  toastError,
  fillByLabel,
  submitFormIn,
} from "@/test";
import { ME_KEY } from "@/hooks/useAuth";

// Sonner emits portals — assert on the spy, not on the rendered toast,
// because the rendered toast is async and stale across reflows. The
// sonner mock + spies are provided by the harness; afterEach in the
// setup file resets all toast spies between tests (#74).

// Inputs are filled via the shared `fillByLabel(label, value)` helper
// (label-based after #132; lifted from per-file shims in #135). Each
// `it` captures container locally via `const { container, ... } =
// renderWithProviders(...)` so module-level `let container` no longer
// exists — single destructure shape across the file. `submitFormIn`
// keeps the multi-form guard (#75); `queryClient` is destructured at
// the call site when an `it` reads the cache (e.g. the happy-path test
// asserting `useLogin.onSuccess` seeded ME_KEY).

describe("LoginPage — validation (#35)", () => {
  it("renders the form with email + password inputs and a sign-in button", () => {
    renderWithProviders(<LoginPage />, { auth: null });
    expect(
      screen.getByRole("heading", { name: /welcome back/i }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText(/^email$/i)).toHaveAttribute("type", "email");
    expect(screen.getByLabelText(/^password$/i)).toHaveAttribute("type", "password");
    expect(screen.getByRole("button", { name: /sign in/i })).toBeInTheDocument();
  });

  it("flags an invalid email format with the user-facing copy", async () => {
    const { container } = renderWithProviders(<LoginPage />, { auth: null });
    fillByLabel(/^email$/i, "not-an-email");
    fillByLabel(/^password$/i, "anything");
    submitFormIn(container);

    await waitFor(() =>
      expect(screen.getByText(/enter a valid email/i)).toBeInTheDocument(),
    );
    // Zod prevented the fetch — no toast, no network call.
    expect(toastError).not.toHaveBeenCalled();
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("flags an empty password with the user-facing copy", async () => {
    const { container } = renderWithProviders(<LoginPage />, { auth: null });
    fillByLabel(/^email$/i, "owner@acme.test");
    submitFormIn(container);

    await waitFor(() =>
      expect(screen.getByText(/password is required/i)).toBeInTheDocument(),
    );
  });
});

describe("LoginPage — happy path (#35)", () => {
  it("on 200 routes to /dashboard, toasts welcome, and primes the Me cache", async () => {
    server.use(http.post(url("/api/auth/login"), () => jsonOk(authedMe)));

    const pushSpy = vi.fn();
    const { container, queryClient } = renderWithProviders(<LoginPage />, {
      auth: null,
      router: { push: pushSpy },
    });

    fillByLabel(/^email$/i, "owner@acme.test");
    fillByLabel(/^password$/i, "verystrongpass1");
    submitFormIn(container);

    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Welcome back!"),
    );
    expect(pushSpy).toHaveBeenCalledWith("/dashboard");
    // useLogin's onSuccess writes the Me into both auth-keys (see useAuth.ts).
    // Use the exported ME_KEY so a rename in useAuth.ts fails this test
    // instead of silently matching `undefined` against the literal.
    expect(queryClient.getQueryData([...ME_KEY])).toMatchObject({
      email: authedMe.email,
    });
  });
});

// Each row pairs a backend envelope with the EXACT user-facing copy that
// should reach the toast. The "raw code that must NOT appear" column
// pins the contract: lib/api.ts maps body.error.message → ApiError.message,
// and the form forwards err.message to toast.error. A regression that
// dropped the message and fell back to err.code (or err.status) would be
// caught here.
const errorCases: ReadonlyArray<{
  label: string;
  status: number;
  code: string;
  message: string;
}> = [
  {
    label: "401 invalid credentials",
    status: 401,
    code: "auth.invalid_credentials",
    message: "Invalid email or password.",
  },
  {
    label: "423 account locked",
    status: 423,
    code: "auth.locked",
    message: "Account temporarily locked. Try again later.",
  },
  {
    label: "400 validation",
    status: 400,
    code: "validation.email",
    message: "Email is required.",
  },
  {
    label: "500 server error",
    status: 500,
    code: "server.error",
    message: "Something went wrong on our end.",
  },
];

describe("LoginPage — server-side error copy (#35)", () => {
  it.each(errorCases)(
    "$label surfaces the human message, NEVER the raw code or status",
    async ({ status, code, message }) => {
      server.use(
        http.post(url("/api/auth/login"), () =>
          jsonError(code, message, { status }),
        ),
      );

      const pushSpy = vi.fn();
      const { container } = renderWithProviders(<LoginPage />, {
        auth: null,
        router: { push: pushSpy },
      });

      fillByLabel(/^email$/i, "owner@acme.test");
      fillByLabel(/^password$/i, "anything");
      submitFormIn(container);

      await waitFor(() =>
        expect(toastError).toHaveBeenCalledWith(message),
      );
      // Jargon-free guard class: the toHaveBeenCalledWith(message) above
      // already pins the EXACT copy for THIS row. These regex bands catch
      // the broader class of regression — a future code (not in this
      // table) leaking through as `auth.x` (dot-namespaced),
      // `E_AUTH_LOCKED` (SCREAMING_SNAKE), or "423" (bare status).
      const toastText = (toastError.mock.calls[0][0] ?? "") as string;
      expect(toastText).not.toContain(code);
      expect(toastText).not.toMatch(/(?:[a-z]+\.)+[a-z_]+/i);
      expect(toastText).not.toMatch(/^[A-Z][A-Z_]{2,}$/);
      expect(toastText).not.toMatch(/^\d{3}$/);
      // The success branch must NOT have fired on any error.
      expect(toastSuccess).not.toHaveBeenCalled();
      expect(pushSpy).not.toHaveBeenCalled();
    },
  );
});

describe("LoginPage — loading state (#35)", () => {
  it("disables the submit button + flips copy to Signing in… while the mutation is pending", async () => {
    // Hold the response so the test sees the isPending branch.
    let release: () => void = () => {};
    const settled = new Promise<void>((r) => (release = r));
    server.use(
      http.post(url("/api/auth/login"), async () => {
        await settled;
        return jsonOk(authedMe);
      }),
    );

    const { container } = renderWithProviders(<LoginPage />, { auth: null });
    fillByLabel(/^email$/i, "owner@acme.test");
    fillByLabel(/^password$/i, "verystrongpass1");
    submitFormIn(container);

    // try/finally so release() runs even if the disabled-button assertion
    // throws — otherwise the MSW handler stays awaiting `settled` forever.
    try {
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: /signing in/i }),
        ).toBeDisabled(),
      );
    } finally {
      release();
    }
    // Await observable settlement so the mutation's onSuccess can't fire
    // mid-afterEach against an unmounted tree.
    await waitFor(() => expect(toastSuccess).toHaveBeenCalled());
  });

  it("offers a 'Forgot your password?' reset path to /forgot-password (#183)", () => {
    renderWithProviders(<LoginPage />, { auth: null });
    expect(screen.getByRole("link", { name: /forgot your password/i }))
      .toHaveAttribute("href", "/forgot-password");
  });
});
