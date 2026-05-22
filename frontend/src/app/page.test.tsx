import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import Home from "./page";

// next/link needs a router context in a real app; in unit tests render it as a plain anchor
// so we can assert exactly which routes the landing-page CTAs point at.
vi.mock("next/link", () => ({
  __esModule: true,
  default: ({ children, href, ...rest }: { children: ReactNode; href: string }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

// Control the auth state the landing page sees via useMe() (the homepage is a client component).
const { mockUseMe } = vi.hoisted(() => ({ mockUseMe: vi.fn() }));
vi.mock("@/hooks/useAuth", () => ({ useMe: mockUseMe }));

const linkHrefs = () => screen.getAllByRole("link").map((el) => el.getAttribute("href"));

describe("Landing page CTAs", () => {
  beforeEach(() => vi.clearAllMocks());

  it("routes an anonymous visitor to /register and /login, with no waitlist gate", () => {
    mockUseMe.mockReturnValue({ data: null });
    render(<Home />);

    const hrefs = linkHrefs();
    expect(hrefs).toContain("/login");
    expect(hrefs).toContain("/register");
    // every pricing CTA carries its plan
    expect(hrefs).toContain("/register?plan=free");
    expect(hrefs).toContain("/register?plan=pro");
    expect(hrefs).toContain("/register?plan=annual");
    // the waitlist gate is gone, in links and in copy
    expect(hrefs).not.toContain("#waitlist");
    expect(screen.queryByText(/waitlist/i)).toBeNull();
  });

  it("defaults to the logged-out CTAs while the session is still loading", () => {
    // useMe() is undefined during SSR / first paint; the public nav must not block on it.
    mockUseMe.mockReturnValue({ data: undefined });
    render(<Home />);

    const hrefs = linkHrefs();
    expect(hrefs).toContain("/login");
    expect(hrefs).not.toContain("/dashboard");
  });

  it("swaps the auth-aware CTAs to a dashboard path when authenticated", () => {
    mockUseMe.mockReturnValue({
      data: {
        userId: "u1",
        organizationId: "o1",
        email: "owner@example.com",
        fullName: "Owner",
        role: "admin",
        plan: "pro",
        organizationName: "Acme",
        timeZone: "UTC",
      },
    });
    render(<Home />);

    const hrefs = linkHrefs();
    expect(hrefs).toContain("/dashboard");
    // "Log in" lives only in the nav + final-CTA logged-out branches, so its absence proves both
    // swapped. (The hero/pricing "Get started" CTAs are intentionally not auth-gated, so a bare
    // /register still appears — asserting its absence here would be wrong.)
    expect(hrefs).not.toContain("/login");
  });
});
