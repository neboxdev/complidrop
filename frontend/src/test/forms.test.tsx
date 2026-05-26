/**
 * Pins the label-input wiring contract added in #76.
 *
 * Every form whose `<label>` text is visible to a user must associate
 * that label with its input via `htmlFor` + `id` — without it, screen
 * readers announce the input with no field-name context AND RTL's
 * `getByLabelText` does NOT resolve the input. After #76 the auth
 * forms (login, register) and the dashboard forms (vendors create,
 * vendor detail) wire every label this way; this test catches a future
 * regression where someone copy-pastes a new field without the
 * htmlFor/id pair.
 *
 * Strategy: render each form, query each known label by accessible
 * text, assert it resolves an input element. If a label loses its
 * htmlFor/id, `getByLabelText` returns null and the test fails with a
 * useful message identifying the form + label.
 */
import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import LoginPage from "@/app/(auth)/login/page";
import RegisterForm from "@/app/(auth)/register/register-form";
import VendorsPage from "@/app/(dashboard)/vendors/page";
import { renderWithProviders, authedMe, server, url, jsonOk } from "@/test";
import { http } from "msw";

describe("form labels wired via htmlFor + id (#76)", () => {
  it("LoginPage: every visible label resolves an input via getByLabelText", () => {
    renderWithProviders(<LoginPage />, { auth: null });

    expect(screen.getByLabelText(/^email$/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/^password$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
  });

  it("RegisterForm: every visible label resolves an input via getByLabelText", () => {
    renderWithProviders(<RegisterForm />, { auth: null });

    expect(screen.getByLabelText(/^full name$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^company$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^work email$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^password$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^industry$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^size$/i)).toBeInstanceOf(HTMLInputElement);
  });

  it("VendorsPage create form: every visible label resolves an input via getByLabelText", async () => {
    // VendorsPage fires GET /api/vendors on mount; default 401 handler
    // would surface as the error card and the add-vendor form would
    // still render alongside it. Seed an empty list so the page lands
    // on its happy path.
    server.use(http.get(url("/api/vendors"), () => jsonOk([])));
    renderWithProviders(<VendorsPage />, { auth: authedMe });

    expect(screen.getByLabelText(/^name$/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/^contact email$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
  });
});
