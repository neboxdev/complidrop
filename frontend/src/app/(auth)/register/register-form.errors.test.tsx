/**
 * Register page — full-stack server-error component test against the
 * harness (#35). Pairs with `register-form.test.tsx` (#31, plan param) and
 * `page.test.tsx` (Suspense wrapper).
 *
 * The plan-param tests in register-form.test.tsx mock `useRegister`
 * directly because they're about the form's prop wiring; this file does
 * the OPPOSITE — it drives the full stack (form → useRegister → api.ts →
 * MSW envelope → ApiError → toast) so a regression at ANY layer (envelope
 * shape, message mapping, mutation error path, toast wiring) fails the
 * test. Two test files for one component, by design — they pin two
 * different contracts.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
import RegisterForm from "./register-form";
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

// sonner mock + spies are provided by the harness; afterEach in the
// setup file resets all toast spies between tests (#74).

// Inputs are filled via the shared `fillByLabel(label, value)` helper
// (label-based after #132; lifted from per-file shims in #135). Each
// `it` captures container locally via `const { container, ... } =
// renderWithProviders(...)` so module-level `let container` no longer
// exists — single destructure shape across the file. `submitFormIn`
// keeps the multi-form guard (#75); `queryClient` is destructured at
// the call site when an `it` reads the cache.

function fillFullForm() {
  fillByLabel(/^full name$/i, "Owner Name");
  fillByLabel(/^company$/i, "Acme Inc");
  fillByLabel(/^work email$/i, "owner@acme.test");
  fillByLabel(/^password$/i, "verystrongpass1");
}

// Validation copy lives in zod schema (`register-form.tsx`). Test the
// client-side branches before we exercise the server-side ones.
describe("RegisterForm — validation (#35)", () => {
  it("flags a short password with the user-facing copy", async () => {
    const { container } = renderWithProviders(<RegisterForm />, { auth: null });
    fillByLabel(/^full name$/i, "Owner Name");
    fillByLabel(/^company$/i, "Acme Inc");
    fillByLabel(/^work email$/i, "owner@acme.test");
    fillByLabel(/^password$/i, "short1");
    submitFormIn(container);

    await waitFor(() =>
      expect(
        screen.getByText(/password must be at least 12 characters/i),
      ).toBeInTheDocument(),
    );
    expect(toastError).not.toHaveBeenCalled();
  });

  it("flags a missing letter in the password", async () => {
    const { container } = renderWithProviders(<RegisterForm />, { auth: null });
    fillByLabel(/^full name$/i, "Owner Name");
    fillByLabel(/^company$/i, "Acme Inc");
    fillByLabel(/^work email$/i, "owner@acme.test");
    fillByLabel(/^password$/i, "123456789012");
    submitFormIn(container);

    await waitFor(() =>
      expect(
        screen.getByText(/password must include a letter/i),
      ).toBeInTheDocument(),
    );
  });

  it("flags a missing digit in the password", async () => {
    const { container } = renderWithProviders(<RegisterForm />, { auth: null });
    fillByLabel(/^full name$/i, "Owner Name");
    fillByLabel(/^company$/i, "Acme Inc");
    fillByLabel(/^work email$/i, "owner@acme.test");
    fillByLabel(/^password$/i, "abcdefghijklm");
    submitFormIn(container);

    await waitFor(() =>
      expect(
        screen.getByText(/password must include a digit/i),
      ).toBeInTheDocument(),
    );
  });

  it("flags a missing full name with the user-facing copy", async () => {
    const { container } = renderWithProviders(<RegisterForm />, { auth: null });
    fillByLabel(/^company$/i, "Acme Inc");
    fillByLabel(/^work email$/i, "owner@acme.test");
    fillByLabel(/^password$/i, "verystrongpass1");
    submitFormIn(container);

    await waitFor(() =>
      expect(
        screen.getByText(/your full name is required/i),
      ).toBeInTheDocument(),
    );
  });
});

describe("RegisterForm — happy path (#35)", () => {
  it("on 200 toasts a welcome, routes to /dashboard, and primes the Me cache", async () => {
    let receivedBody: Record<string, unknown> | undefined;
    server.use(
      http.post(url("/api/auth/register"), async ({ request }) => {
        receivedBody = (await request.json()) as Record<string, unknown>;
        return jsonOk(authedMe);
      }),
    );

    const pushSpy = vi.fn();
    const { container, queryClient } = renderWithProviders(<RegisterForm />, {
      auth: null,
      router: { push: pushSpy },
    });
    fillFullForm();
    submitFormIn(container);

    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Account created. Welcome!"),
    );
    expect(pushSpy).toHaveBeenCalledWith("/dashboard");
    expect(queryClient.getQueryData([...ME_KEY])).toMatchObject({
      email: authedMe.email,
    });

    // Body contract: form forwards every field the backend RegisterRequest
    // accepts (fullName + companyName + email + password + timeZone). The
    // IANA timezone is derived client-side via Intl; pin the SHAPE not the
    // value (different CI hosts report different default zones).
    expect(receivedBody).toMatchObject({
      fullName: "Owner Name",
      companyName: "Acme Inc",
      email: "owner@acme.test",
      password: "verystrongpass1",
    });
    expect(receivedBody?.timeZone).toMatch(/^(UTC|.+\/.+)$/);
  });

  // Pins the #31 contract from the OTHER direction: register-form.test.tsx
  // mocks useRegister and asserts the form NEVER calls it with `plan`; this
  // test exercises the actual wire — with ?plan=annual ACTUALLY set, the
  // form must (a) reflect it in the UI and (b) NOT forward it to the
  // backend. Without (a) the (b) assertion would be a tautology (nothing
  // could forward what wasn't read).
  it("with ?plan=annual: reflects it in UI but does NOT forward plan to /api/auth/register", async () => {
    let receivedBody: Record<string, unknown> | undefined;
    server.use(
      http.post(url("/api/auth/register"), async ({ request }) => {
        receivedBody = (await request.json()) as Record<string, unknown>;
        return jsonOk(authedMe);
      }),
    );

    const { container } = renderWithProviders(<RegisterForm />, {
      auth: null,
      router: { push: vi.fn() },
      searchParams: { plan: "annual" },
    });

    // UI side: heading + upsell banner reflect annual.
    expect(
      screen.getByRole("heading", { name: /annual account/i }),
    ).toBeInTheDocument();
    expect(screen.getByText(/you selected the annual plan/i)).toBeInTheDocument();

    fillFullForm();
    submitFormIn(container);
    await waitFor(() => expect(toastSuccess).toHaveBeenCalled());

    // Wire side: plan absent from the POST body, even though useSearchParams
    // saw it. The backend DTO doesn't accept it (#31 Non-goals — billing
    // remains a separate ticket).
    expect(receivedBody).not.toHaveProperty("plan");
  });
});

const serverErrorCases: ReadonlyArray<{
  label: string;
  status: number;
  code: string;
  message: string;
}> = [
  {
    label: "409 duplicate email",
    status: 409,
    code: "auth.email_taken",
    message: "An account with that email already exists.",
  },
  {
    label: "400 missing fields",
    status: 400,
    code: "validation.required",
    message: "Full name and company name are required.",
  },
  {
    label: "400 invalid email",
    status: 400,
    code: "validation.email",
    message: "Enter a valid email.",
  },
  {
    label: "500 server error",
    status: 500,
    code: "server.error",
    message: "Something went wrong on our end.",
  },
];

describe("RegisterForm — server-side error copy (#35)", () => {
  it.each(serverErrorCases)(
    "$label surfaces the human message, NEVER the raw code or status",
    async ({ status, code, message }) => {
      server.use(
        http.post(url("/api/auth/register"), () =>
          jsonError(code, message, { status }),
        ),
      );

      const pushSpy = vi.fn();
      const { container } = renderWithProviders(<RegisterForm />, {
        auth: null,
        router: { push: pushSpy },
      });
      fillFullForm();
      submitFormIn(container);

      await waitFor(() =>
        expect(toastError).toHaveBeenCalledWith(message),
      );
      const toastText = (toastError.mock.calls[0][0] ?? "") as string;
      // Jargon-free guard class: no dot-namespaced (auth.x, validation.y),
      // no SCREAMING_SNAKE error codes, no bare 3-digit status string.
      // The toHaveBeenCalledWith(message) above already pins the EXACT
      // copy; these regex bands catch the broader class of regression
      // where a future code (not in this table) leaked through.
      expect(toastText).not.toContain(code);
      expect(toastText).not.toMatch(/(?:[a-z]+\.)+[a-z_]+/i);
      expect(toastText).not.toMatch(/^[A-Z][A-Z_]{2,}$/);
      expect(toastText).not.toMatch(/^\d{3}$/);
      expect(toastSuccess).not.toHaveBeenCalled();
      expect(pushSpy).not.toHaveBeenCalled();
    },
  );
});

describe("RegisterForm — loading state (#35)", () => {
  it("disables the submit button + flips copy while the mutation is pending", async () => {
    let release: () => void = () => {};
    const settled = new Promise<void>((r) => (release = r));
    server.use(
      http.post(url("/api/auth/register"), async () => {
        await settled;
        return jsonOk(authedMe);
      }),
    );

    const { container } = renderWithProviders(<RegisterForm />, { auth: null });
    fillFullForm();
    submitFormIn(container);

    // try/finally so release() runs even if the disabled-button assertion
    // throws — otherwise the MSW handler stays awaiting `settled` forever
    // and the leaked closure is held across the next test's cleanup.
    try {
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: /creating account/i }),
        ).toBeDisabled(),
      );
    } finally {
      release();
    }
    // Await observable settlement so the mutation's onSuccess can't fire
    // mid-afterEach against an unmounted tree.
    await waitFor(() => expect(toastSuccess).toHaveBeenCalled());
  });
});
