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
import { describe, it, expect, vi, beforeEach } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import RegisterForm from "./register-form";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
} from "@/test";

const { toastSuccess, toastError } = vi.hoisted(() => ({
  toastSuccess: vi.fn(),
  toastError: vi.fn(),
}));
vi.mock("sonner", () => ({
  toast: { success: toastSuccess, error: toastError },
  Toaster: () => null,
}));

function fillField(name: string, value: string) {
  const input = document.querySelector(`input[name="${name}"]`) as HTMLInputElement;
  if (!input) throw new Error(`no input named "${name}"`);
  fireEvent.input(input, { target: { value } });
}

function fillFullForm() {
  fillField("fullName", "Owner Name");
  fillField("companyName", "Acme Inc");
  fillField("email", "owner@acme.test");
  fillField("password", "verystrongpass1");
}

function submitForm() {
  const form = document.querySelector("form");
  if (!form) throw new Error("no form rendered");
  fireEvent.submit(form);
}

// Validation copy lives in zod schema (`register-form.tsx`). Test the
// client-side branches before we exercise the server-side ones.
describe("RegisterForm — validation (#35)", () => {
  beforeEach(() => {
    toastSuccess.mockClear();
    toastError.mockClear();
  });

  it("flags a short password with the user-facing copy", async () => {
    renderWithProviders(<RegisterForm />, { auth: null });
    fillField("fullName", "Owner Name");
    fillField("companyName", "Acme Inc");
    fillField("email", "owner@acme.test");
    fillField("password", "short1");
    submitForm();

    await waitFor(() =>
      expect(
        screen.getByText(/password must be at least 12 characters/i),
      ).toBeInTheDocument(),
    );
    expect(toastError).not.toHaveBeenCalled();
  });

  it("flags a missing letter in the password", async () => {
    renderWithProviders(<RegisterForm />, { auth: null });
    fillField("fullName", "Owner Name");
    fillField("companyName", "Acme Inc");
    fillField("email", "owner@acme.test");
    fillField("password", "123456789012");
    submitForm();

    await waitFor(() =>
      expect(
        screen.getByText(/password must include a letter/i),
      ).toBeInTheDocument(),
    );
  });

  it("flags a missing digit in the password", async () => {
    renderWithProviders(<RegisterForm />, { auth: null });
    fillField("fullName", "Owner Name");
    fillField("companyName", "Acme Inc");
    fillField("email", "owner@acme.test");
    fillField("password", "abcdefghijklm");
    submitForm();

    await waitFor(() =>
      expect(
        screen.getByText(/password must include a digit/i),
      ).toBeInTheDocument(),
    );
  });

  it("flags a missing full name with the user-facing copy", async () => {
    renderWithProviders(<RegisterForm />, { auth: null });
    fillField("companyName", "Acme Inc");
    fillField("email", "owner@acme.test");
    fillField("password", "verystrongpass1");
    submitForm();

    await waitFor(() =>
      expect(
        screen.getByText(/your full name is required/i),
      ).toBeInTheDocument(),
    );
  });
});

describe("RegisterForm — happy path (#35)", () => {
  beforeEach(() => {
    toastSuccess.mockClear();
    toastError.mockClear();
  });

  it("on 200 toasts a welcome, routes to /dashboard, and primes the Me cache", async () => {
    let receivedBody: Record<string, unknown> | undefined;
    server.use(
      http.post(url("/api/auth/register"), async ({ request }) => {
        receivedBody = (await request.json()) as Record<string, unknown>;
        return jsonOk(authedMe);
      }),
    );

    const pushSpy = vi.fn();
    const { queryClient } = renderWithProviders(<RegisterForm />, {
      auth: null,
      router: { push: pushSpy },
    });
    fillFullForm();
    submitForm();

    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Account created. Welcome!"),
    );
    expect(pushSpy).toHaveBeenCalledWith("/dashboard");
    expect(queryClient.getQueryData(["auth", "me"])).toMatchObject({
      email: authedMe.email,
    });

    // Body contract guard: form forwards every field the backend accepts
    // INCLUDING the IANA timezone, but deliberately NOT `plan` (#31
    // Non-goals — billing is a separate ticket, the backend DTO doesn't
    // accept it).
    expect(receivedBody).toMatchObject({
      fullName: "Owner Name",
      companyName: "Acme Inc",
      email: "owner@acme.test",
      password: "verystrongpass1",
    });
    expect(receivedBody).toHaveProperty("timeZone");
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
  beforeEach(() => {
    toastSuccess.mockClear();
    toastError.mockClear();
  });

  it.each(serverErrorCases)(
    "$label surfaces the human message, NEVER the raw code or status",
    async ({ status, code, message }) => {
      server.use(
        http.post(url("/api/auth/register"), () =>
          jsonError(code, message, { status }),
        ),
      );

      const pushSpy = vi.fn();
      renderWithProviders(<RegisterForm />, {
        auth: null,
        router: { push: pushSpy },
      });
      fillFullForm();
      submitForm();

      await waitFor(() =>
        expect(toastError).toHaveBeenCalledWith(message),
      );
      const toastText = (toastError.mock.calls[0][0] ?? "") as string;
      // No dot-namespaced codes ever reach the toast.
      expect(toastText).not.toContain(code);
      expect(toastText).not.toMatch(/^\d{3}$/);
      expect(toastSuccess).not.toHaveBeenCalled();
      expect(pushSpy).not.toHaveBeenCalled();
    },
  );
});

describe("RegisterForm — loading state (#35)", () => {
  beforeEach(() => {
    toastSuccess.mockClear();
    toastError.mockClear();
  });

  it("disables the submit button + flips copy while the mutation is pending", async () => {
    let release: () => void = () => {};
    const settled = new Promise<void>((r) => (release = r));
    server.use(
      http.post(url("/api/auth/register"), async () => {
        await settled;
        return jsonOk(authedMe);
      }),
    );

    renderWithProviders(<RegisterForm />, { auth: null });
    fillFullForm();
    submitForm();

    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: /creating account/i }),
      ).toBeDisabled(),
    );

    release();
  });
});
