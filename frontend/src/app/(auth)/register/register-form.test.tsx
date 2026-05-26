/**
 * Register page consumes the `?plan=` query param the landing-page pricing CTAs
 * pass (#31). Before this change, register/page.tsx never called
 * useSearchParams, so /register?plan=annual was indistinguishable from /register
 * — the choice was silently dropped between the pricing screen and signup.
 *
 * This test pins the contract:
 *   - free | pro | annual each render plan-aware heading / subtitle / banner copy
 *   - missing param falls back to "free"
 *   - unknown values fall back to "free" (no XSS surface from arbitrary input)
 *
 * The form itself is irrelevant to the param-routing contract, so we mock out
 * useRegister and toast. We render RegisterForm directly (not page.tsx) so the
 * Suspense boundary is out of scope — it exists purely to satisfy Next's
 * production-build requirement for useSearchParams.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import RegisterForm from "./register-form";

vi.mock("next/link", () => ({
  __esModule: true,
  default: ({ children, href, ...rest }: { children: ReactNode; href: string }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

const { mockSearchParams, mockPush } = vi.hoisted(() => ({
  mockSearchParams: vi.fn(),
  mockPush: vi.fn(),
}));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: mockPush }),
  useSearchParams: mockSearchParams,
}));

vi.mock("@/hooks/useAuth", () => ({
  useRegister: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

function setPlanParam(value: string | null) {
  const params = value === null ? new URLSearchParams() : new URLSearchParams({ plan: value });
  mockSearchParams.mockReturnValue(params);
}

describe("RegisterForm — ?plan= consumption (#31)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });
  afterEach(() => cleanup());

  it("reflects ?plan=annual in the heading, subtitle, and banner", () => {
    setPlanParam("annual");
    render(<RegisterForm />);

    expect(screen.getByRole("heading", { name: /annual account/i })).toBeInTheDocument();
    const status = screen.getByRole("status");
    expect(status.textContent).toMatch(/annual plan/i);
    expect(status.textContent).toMatch(/\$39/);
    expect(status.textContent).toMatch(/\$468/);
    // user can correct a misclick from the pricing screen
    expect(screen.getByRole("link", { name: /change/i })).toHaveAttribute("href", "/#pricing");
  });

  it("reflects ?plan=pro in the heading and banner", () => {
    setPlanParam("pro");
    render(<RegisterForm />);

    expect(screen.getByRole("heading", { name: /pro account/i })).toBeInTheDocument();
    const status = screen.getByRole("status");
    expect(status.textContent).toMatch(/pro plan/i);
    expect(status.textContent).toMatch(/\$49/);
  });

  it("renders the free-tier copy (and no upsell banner) for ?plan=free", () => {
    setPlanParam("free");
    render(<RegisterForm />);

    expect(screen.getByRole("heading", { name: /start dropping docs/i })).toBeInTheDocument();
    expect(screen.getByText(/free forever for 5 documents/i)).toBeInTheDocument();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("defaults to the free-tier copy when no plan param is present", () => {
    setPlanParam(null);
    render(<RegisterForm />);

    expect(screen.getByRole("heading", { name: /start dropping docs/i })).toBeInTheDocument();
    expect(screen.getByText(/free forever for 5 documents/i)).toBeInTheDocument();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("falls back to the free-tier copy for an unknown ?plan= value", () => {
    setPlanParam("enterprise"); // not in the allowed set
    render(<RegisterForm />);

    expect(screen.getByRole("heading", { name: /start dropping docs/i })).toBeInTheDocument();
    expect(screen.queryByRole("status")).toBeNull();
  });
});
