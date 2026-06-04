/**
 * Register page consumes the `?plan=` query param the landing-page pricing CTAs
 * pass (#31). Before this change, register/page.tsx never called
 * useSearchParams, so /register?plan=annual was indistinguishable from /register
 * — the choice was silently dropped between the pricing screen and signup.
 *
 * This file pins three contracts:
 *
 * 1. UI reflects the plan choice
 *    free | pro | annual each render plan-aware heading / subtitle / banner.
 *    Missing / unknown / cased / whitespace-padded values fall back to free
 *    so we never render a heading for a plan we don't have configured copy for.
 *
 * 2. The plan choice is deliberately NOT sent to /api/auth/register
 *    The backend RegisterRequest DTO doesn't accept `plan` today and billing
 *    is out of scope for #31. The submission-payload test pins that contract
 *    so a future Stripe-wiring change can't silently start forwarding an
 *    unvalidated value.
 *
 * 3. The full form still submits normally
 *    Existing form fields haven't regressed and the mutation receives the
 *    expected user-supplied payload.
 *
 * Tests render RegisterForm directly (not page.tsx) — the Suspense wrapper
 * has its own smoke test in register/page.test.tsx.
 */
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import type { ReactNode } from "react";
import RegisterForm, { PASSWORD_RULES, schema } from "./register-form";
import { fillByLabel, submitFormIn } from "@/test";

vi.mock("next/link", () => ({
  __esModule: true,
  default: ({ children, href, ...rest }: { children: ReactNode; href: string }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

const { mockSearchParams, mockPush, mockMutateAsync } = vi.hoisted(() => ({
  mockSearchParams: vi.fn(),
  mockPush: vi.fn(),
  mockMutateAsync: vi.fn(),
}));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: mockPush }),
  useSearchParams: mockSearchParams,
}));

vi.mock("@/hooks/useAuth", () => ({
  useRegister: () => ({ mutateAsync: mockMutateAsync, isPending: false }),
}));

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

function setPlanParam(value: string | null) {
  const params = value === null ? new URLSearchParams() : new URLSearchParams({ plan: value });
  mockSearchParams.mockReturnValue(params);
}

describe("RegisterForm — ?plan= consumption (#31)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockMutateAsync.mockResolvedValue({});
  });

  // Dollar values are asserted directly because they are load-bearing UX:
  // the user picked Annual to save $120, so confirming the math on the
  // register screen IS the test contract. An intentional pricing change
  // should fail this test and force the copy update on both screens.
  it("reflects ?plan=annual in the heading, subtitle, and banner", () => {
    setPlanParam("annual");
    render(<RegisterForm />);

    expect(screen.getByRole("heading", { name: /annual account/i })).toBeInTheDocument();
    const banner = screen.getByText(/you selected the/i);
    expect(banner.textContent).toMatch(/annual plan/i);
    expect(banner.textContent).toMatch(/\$39/);
    expect(banner.textContent).toMatch(/\$468/);
    // user can correct a misclick from the pricing screen
    expect(screen.getByRole("link", { name: /change/i })).toHaveAttribute("href", "/#pricing");
  });

  it("reflects ?plan=pro in the heading and banner", () => {
    setPlanParam("pro");
    render(<RegisterForm />);

    expect(screen.getByRole("heading", { name: /pro account/i })).toBeInTheDocument();
    const banner = screen.getByText(/you selected the/i);
    expect(banner.textContent).toMatch(/pro plan/i);
    expect(banner.textContent).toMatch(/\$49/);
  });

  it("renders the free-tier copy (and no upsell banner) for ?plan=free", () => {
    setPlanParam("free");
    render(<RegisterForm />);

    expect(screen.getByRole("heading", { name: /start dropping docs/i })).toBeInTheDocument();
    expect(screen.getByText(/free forever for 5 documents/i)).toBeInTheDocument();
    expect(screen.queryByText(/you selected the/i)).toBeNull();
  });

  it("stacks the two-up field grids on a phone (responsive, #181)", () => {
    setPlanParam("free");
    const { container } = render(<RegisterForm />);

    // The two-up rows (full name / company, industry / size) collapse to a
    // single column below sm so inputs aren't squeezed to ~150px on a 390px
    // phone. Class-presence proxy (JSDOM applies no stylesheet).
    const stackedGrids = container.querySelectorAll(
      ".grid.grid-cols-1.sm\\:grid-cols-2",
    );
    expect(stackedGrids.length).toBeGreaterThanOrEqual(2);
  });

  it("defaults to the free-tier copy when no plan param is present", () => {
    setPlanParam(null);
    render(<RegisterForm />);

    expect(screen.getByRole("heading", { name: /start dropping docs/i })).toBeInTheDocument();
    expect(screen.getByText(/free forever for 5 documents/i)).toBeInTheDocument();
    expect(screen.queryByText(/you selected the/i)).toBeNull();
  });

  // Plan parsing is tolerant of mixed case + surrounding whitespace (real
  // links shared via email / Slack / marketing copy occasionally arrive
  // capitalized). Anything outside the allowlist still falls through to
  // the free-tier copy so we never render a heading for an unknown plan.
  it.each([
    ["empty value (?plan=)", ""],
    ["uppercase ?plan=PRO", "PRO"],
    ["title case ?plan=Annual", "Annual"],
    ["whitespace-padded ? plan= pro ", " pro "],
    ["totally unknown ?plan=enterprise", "enterprise"],
    ["random garbage ?plan=<script>alert(1)</script>", "<script>alert(1)</script>"],
  ])("%s — normalizes or falls through to free copy", (_label, value) => {
    setPlanParam(value);
    render(<RegisterForm />);

    // The cased / whitespace cases SHOULD resolve to the real plan and show
    // a banner; the empty / unknown / garbage cases should NOT.
    const normalized = value.trim().toLowerCase();
    const isKnown = ["free", "pro", "annual"].includes(normalized);
    if (isKnown && normalized !== "free") {
      expect(screen.getByText(/you selected the/i)).toBeInTheDocument();
    } else {
      expect(screen.getByRole("heading", { name: /start dropping docs/i })).toBeInTheDocument();
      expect(screen.queryByText(/you selected the/i)).toBeNull();
    }
    // Garbage value must not reach the DOM as markup, regardless of parse outcome.
    expect(document.querySelector("script")).toBeNull();
  });
});

describe("RegisterForm — submission payload (#31 Non-goals: no billing)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockMutateAsync.mockResolvedValue({});
  });

  // Inputs filled via shared `fillByLabel` helper from `@/test` (#135
  // lifted the previously per-file shim, label-based after #132). The
  // container capture stays local because `submitFormIn` retains the
  // multi-form guard from #75. Single destructure shape across the
  // file — no module-level `let container`.

  it("does not forward the selected plan to /api/auth/register", async () => {
    setPlanParam("annual");
    const { container } = render(<RegisterForm />);

    fillByLabel(/^full name$/i, "Owner Name");
    fillByLabel(/^company$/i, "Acme Inc");
    fillByLabel(/^email$/i, "owner@example.com");
    fillByLabel(/^password$/i, "verystrong1pass");

    submitFormIn(container);

    await waitFor(() => expect(mockMutateAsync).toHaveBeenCalled());
    const payload = mockMutateAsync.mock.calls[0][0] as Record<string, unknown>;

    // The deliberately-NOT-forwarded fields:
    expect(payload).not.toHaveProperty("plan");

    // The fields the backend actually accepts (matches RegisterRequest DTO).
    // Migrating to getByLabelText means the EMAIL field comes from the
    // "Email" label — pin that the form key is still `email` (the backend
    // DTO), so a future label rename can't silently change the wire payload
    // shape.
    expect(payload).toMatchObject({
      fullName: "Owner Name",
      companyName: "Acme Inc",
      email: "owner@example.com",
      password: "verystrong1pass",
    });
    // timeZone is derived client-side from Intl, asserted as present (value
    // varies by test runner host so don't pin a literal).
    expect(payload).toHaveProperty("timeZone");
  });
});

describe("RegisterForm — signup-friction reducers (#195)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockMutateAsync.mockResolvedValue({});
    setPlanParam(null);
  });

  it("marks Industry and Size as optional", () => {
    render(<RegisterForm />);
    expect(screen.getByText(/^industry/i).textContent).toMatch(/optional/i);
    expect(screen.getByText(/^size/i).textContent).toMatch(/optional/i);
  });

  it("reassures free/no-card on the free default but never on a paid plan", () => {
    setPlanParam(null); // default → free
    const { rerender } = render(<RegisterForm />);
    expect(
      screen.getByText(/free for your first 5 documents\. no credit card/i),
    ).toBeInTheDocument();

    // A paid plan must NOT promise "free, no card" — that would contradict the
    // "$49/month" banner above it. (#195 review — user-empathy)
    setPlanParam("pro");
    rerender(<RegisterForm />);
    expect(screen.queryByText(/no credit card/i)).toBeNull();
    expect(screen.getByText(/cancel anytime\. no long-term contract/i)).toBeInTheDocument();
  });

  it("shows a live password checklist that flips each rule's met-state as it's satisfied", () => {
    render(<RegisterForm />);
    // All three rules are visible up-front — guidance, not a post-submit rejection.
    expect(screen.getByText(/at least 12 characters/i)).toBeInTheDocument();
    expect(screen.getByText(/^a letter$/i)).toBeInTheDocument();
    expect(screen.getByText(/^a number$/i)).toBeInTheDocument();

    const pw = screen.getByLabelText(/^password$/i);
    // Assert on the refactor-stable data-met attribute, not the color class.
    // Letters only, too short: "a letter" met, length + number not.
    fireEvent.change(pw, { target: { value: "abc" } });
    expect(screen.getByText(/^a letter$/i)).toHaveAttribute("data-met", "true");
    expect(screen.getByText(/at least 12 characters/i)).toHaveAttribute("data-met", "false");
    expect(screen.getByText(/^a number$/i)).toHaveAttribute("data-met", "false");

    // A fully-valid password turns all three met.
    fireEvent.change(pw, { target: { value: "abcdefgh1234" } });
    expect(screen.getByText(/at least 12 characters/i)).toHaveAttribute("data-met", "true");
    expect(screen.getByText(/^a letter$/i)).toHaveAttribute("data-met", "true");
    expect(screen.getByText(/^a number$/i)).toHaveAttribute("data-met", "true");
  });

  it("keeps the checklist rules aligned with the zod schema (drift guard)", () => {
    // The checklist (PASSWORD_RULES) and the zod schema are two copies of the
    // same constraints; this fails CI if one drifts so the checklist can never
    // show all-green for a password the schema then rejects. (#195 review)
    const allRulesPass = (v: string) => PASSWORD_RULES.every((r) => r.test(v));
    const zodPasses = (v: string) => schema.shape.password.safeParse(v).success;
    for (const v of [
      "abcdefgh1234", // valid: 12 + letter + digit
      "abcdefghijk", //  11 chars → too short
      "abcdefghijkl", // 12 letters, no digit
      "123456789012", // 12 digits, no letter
      "abc1", //         too short
      "", //             empty
    ]) {
      expect(allRulesPass(v)).toBe(zodPasses(v));
    }
  });
});
