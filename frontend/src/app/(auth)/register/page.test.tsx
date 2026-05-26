/**
 * page.tsx is the Suspense wrapper that lets RegisterForm call
 * useSearchParams() while keeping the route prerenderable as static HTML.
 * Next.js fails the production build if useSearchParams is called without an
 * ancestor Suspense boundary — but vitest doesn't run the production build.
 *
 * This file pins TWO contracts independently of the build:
 *
 *   1. Structural — RegisterPage()'s root element IS a React.Suspense.
 *      Refactoring page.tsx to inline useSearchParams into a server component
 *      (which would still pass `render(<RegisterPage/>)` with mocked
 *      navigation) breaks this assertion before it breaks `next build` in CI.
 *
 *   2. Happy-path — the wrapper resolves and the child form renders.
 */
import { describe, it, expect, vi } from "vitest";
import { isValidElement, Suspense } from "react";
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

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

describe("RegisterPage — Suspense wrapper (#31)", () => {
  it("returns a Suspense element at its root (structural contract)", () => {
    const element = RegisterPage();
    expect(isValidElement(element)).toBe(true);
    // element.type is the component reference, not a string. Comparing
    // directly to React.Suspense catches the regression where someone
    // refactors page.tsx and drops the Suspense wrapper — `next build` will
    // also catch it via the "Missing Suspense boundary with useSearchParams"
    // error, but this fails the vitest run BEFORE CI hits the prod build.
    expect((element as { type: unknown }).type).toBe(Suspense);
  });

  it("renders the form inside its Suspense boundary (happy-path)", () => {
    render(<RegisterPage />);
    expect(
      screen.getByRole("heading", { name: /start dropping docs/i }),
    ).toBeInTheDocument();
  });
});
