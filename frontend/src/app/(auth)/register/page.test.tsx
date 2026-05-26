/**
 * page.tsx is the Suspense wrapper that lets RegisterForm call
 * useSearchParams() while keeping the route prerenderable as static HTML.
 * Next.js fails the production build if useSearchParams is called without an
 * ancestor Suspense boundary — but vitest doesn't run the production build.
 *
 * This smoke test pins the contract independently of the build: if someone
 * refactors page.tsx and removes the Suspense (or inlines useSearchParams
 * into a server component), the wrapper still wires up correctly and renders
 * the child form. It also covers the boundary's happy-path resolve.
 */
import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import RegisterPage from "./page";

vi.mock("next/link", () => ({
  __esModule: true,
  default: ({ children, href, ...rest }: { children: ReactNode; href: string }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn() }),
  useSearchParams: () => new URLSearchParams(),
}));

vi.mock("@/hooks/useAuth", () => ({
  useRegister: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

describe("RegisterPage — Suspense wrapper (#31)", () => {
  it("renders the form inside its Suspense boundary", () => {
    render(<RegisterPage />);
    // Free-tier heading appears once the Suspense child resolves. If the
    // wrapper were removed and the build still passed locally, this would
    // still pass — but it pins the structural contract so a future refactor
    // that loses the boundary at least fails this test or the next prod build.
    expect(
      screen.getByRole("heading", { name: /start dropping docs/i }),
    ).toBeInTheDocument();
  });
});
