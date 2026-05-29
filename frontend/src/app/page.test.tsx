import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import Home from "./page";
// Direct import from the source-of-truth module (#71 followup) —
// the previous test imported from `(auth)/register/register-form`
// via a re-export shim, which crossed route boundaries unnecessarily.
import { KNOWN_PLAN_IDS, PLANS } from "@/lib/plans";

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

// Control the auth state the marketing header sees via useMe(). The homepage is
// now a server component; the auth-aware CTA lives in the <MarketingHeader>
// client island (the page's only client surface), which consumes useMe().
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
    // Every plan the register page knows how to render copy for must have a
    // pricing CTA on the landing page — otherwise the round-trip (#31) is
    // broken on one side: either a CTA emits a plan register-form falls back
    // to free for, or a plan register-form supports has no CTA pointing at it.
    for (const plan of KNOWN_PLAN_IDS) {
      expect(hrefs).toContain(`/register?plan=${plan}`);
    }
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

  it("renders the brand Logo in the hero and footer (header is decorative under the home Link)", () => {
    // The header Logo is decorative (its accessible name comes from the wrapping
    // `<Link aria-label="CompliDrop — home">`). The hero (twotone) and footer
    // (primary) Logos both expose `role="img"` with the CompliDrop label. A
    // regression that removed all three placements would no longer be caught
    // by the link-only assertions above; this test pins that the brand mark
    // is actually rendered in the most visible spots on the marketing site.
    mockUseMe.mockReturnValue({ data: null });
    render(<Home />);

    const brandImages = screen.getAllByRole("img", { name: /CompliDrop/i });
    // Hero + footer at minimum. (The header counts a wrapping `<Link>` with
    // aria-label="CompliDrop — home" as a separate accessible image-or-link
    // depending on the engine; we assert ≥2 to stay implementation-tolerant.)
    expect(brandImages.length).toBeGreaterThanOrEqual(2);
  });
});

describe("Landing page copy (SEO + jargon, #176)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseMe.mockReturnValue({ data: null });
  });

  it("does not surface the 'OCR' tech jargon flagged by the founder", () => {
    render(<Home />);
    // The old "OCR That Can't Read a PDF" card spoke to engineers, not the
    // venue/property-manager buyer. The replacement keeps the pain, drops the
    // acronym. A regression that reintroduces "OCR" should fail here.
    expect(screen.queryByText(/\bOCR\b/i)).toBeNull();
  });

  it("leads with the buyer's search term, not just the brand tagline", () => {
    render(<Home />);
    // The H1 and subhead must carry "certificate of insurance" / "COI" — the
    // words people actually search — so the page can rank and be cited for them.
    // `\bCOI\b` (word-boundary, no /i) pins the literal acronym, not any word
    // that merely contains "coi".
    expect(screen.getAllByText(/certificate of insurance/i).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/\bCOI\b/).length).toBeGreaterThan(0);
  });
});

describe("Homepage structured data (#176)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseMe.mockReturnValue({ data: null });
  });

  it("emits SoftwareApplication JSON-LD carrying the Pro price (end-to-end)", () => {
    const { container } = render(<Home />);
    const nodes = Array.from(
      container.querySelectorAll('script[type="application/ld+json"]'),
    ).map((el) => JSON.parse(el.textContent ?? "{}") as Record<string, unknown>);

    const software = nodes.find((node) => node["@type"] === "SoftwareApplication");
    expect(software, "homepage must render SoftwareApplication JSON-LD").toBeTruthy();
    // Price flows from lib/plans → schema; pin it end-to-end so deleting the
    // node or drifting the price fails here.
    expect((software!.offers as Record<string, unknown>).price).toBe(
      PLANS.pro.monthlyPriceLabel.replace(/[^0-9.]/g, ""),
    );
  });
});
