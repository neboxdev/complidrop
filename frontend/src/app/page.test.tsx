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
    expect(hrefs.some((h) => h?.startsWith("/register"))).toBe(true);
    // pricing CTAs carry the selected plan
    expect(hrefs).toContain("/register?plan=annual");
    expect(hrefs).toContain("/register?plan=pro");
    // the waitlist gate is gone, in links and in copy
    expect(hrefs).not.toContain("#waitlist");
    expect(screen.queryByText(/waitlist/i)).toBeNull();
  });

  it("offers an authenticated visitor a path to the dashboard", () => {
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

    expect(linkHrefs()).toContain("/dashboard");
  });
});
