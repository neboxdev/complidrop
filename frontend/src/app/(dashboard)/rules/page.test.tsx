/**
 * Rules page — tier-3 smoke + the #93 double-invalidate regression pin.
 *
 * The page lists compliance templates and lets the user inspect /
 * edit / delete rules on a selected template. Two layers covered:
 *
 *   1. Smoke (#36): render-without-crash + populated-state surfaces
 *      a template by name, and the empty state renders the new-
 *      template input.
 *
 *   2. Cache-invalidation contract (#93): upsertRule.onSuccess and
 *      deleteRule.onSuccess each must trigger EXACTLY ONE refetch of
 *      the template-detail observer (initial fetch + one
 *      invalidate-driven refetch = 2 total calls), NOT two. The
 *      original code called `invalidateQueries(['templates',
 *      selectedId])` AND `invalidateQueries(['templates'])` —
 *      TanStack Query's default prefix-match meant the broader call
 *      already hit the detail observer, so the explicit narrow call
 *      double-fired the refetch. Pinning exact-2 catches a regression
 *      that re-adds the redundant invalidate, mirroring the #81
 *      pattern from useUpdateVendor.
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import RulesPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  authedMe,
} from "@/test";

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

// User-editable template (NOT a system template — system templates
// hide the New-Rule row and the rule-delete button, both of which
// the cache-invalidation tests need to exercise).
const EDITABLE_TEMPLATE = {
  id: "t_user_01",
  name: "Custom COI Template",
  description: "User-editable COI checklist",
  isSystemTemplate: false,
  ruleCount: 1,
  vendorCount: 0,
};

const TEMPLATE_DETAIL_INITIAL = {
  id: "t_user_01",
  name: "Custom COI Template",
  description: "User-editable COI checklist",
  isSystemTemplate: false,
  rules: [
    {
      id: "r_existing_01",
      documentType: "coi",
      fieldName: "policy_number",
      operator: "required",
      expectedValue: null,
      errorMessage: null,
      sortOrder: 1,
    },
  ],
};

describe("RulesPage — smoke (#36)", () => {
  it("renders the templates list when the API returns at least one", async () => {
    server.use(
      http.get(url("/api/compliance/templates"), () =>
        jsonOk([
          {
            id: "t_default_01",
            name: "Default COI",
            description: "Built-in COI checklist",
            isSystemTemplate: true,
            ruleCount: 5,
            vendorCount: 0,
          },
        ]),
      ),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });

    await waitFor(() =>
      expect(screen.getByText("Default COI")).toBeInTheDocument(),
    );
  });

  it("empty-state: renders the page chrome and the create-template input", async () => {
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });

    // The new-template input is unconditional — a regression that
    // hides the template editor would drop this placeholder.
    expect(
      screen.getByPlaceholderText(/template name/i) ??
        screen.getByPlaceholderText(/new template/i),
    ).toBeInTheDocument();
  });
});

describe("RulesPage — rule mutations prefix-invalidate ['templates'] (#93)", () => {
  it("upsertRule.onSuccess fires exactly ONE detail refetch (not two — #93 regression pin)", async () => {
    // Mirrors the useUpdateVendor exact-2 pattern from #81 at the
    // page layer. The original code's `invalidateQueries(['templates',
    // selectedId])` + `invalidateQueries(['templates'])` combo
    // double-fired the detail refetch per save. With the fix
    // (broader-only invalidate, prefix-match covers detail), the
    // detail observer should refetch exactly once after Add → onSuccess.
    let detailCalls = 0;
    let listCalls = 0;
    server.use(
      http.get(url("/api/compliance/templates"), () => {
        listCalls++;
        return jsonOk([EDITABLE_TEMPLATE]);
      }),
      http.get(url("/api/compliance/templates/:id"), () => {
        detailCalls++;
        return jsonOk(TEMPLATE_DETAIL_INITIAL);
      }),
      http.post(url("/api/compliance/templates/:id/rules"), () =>
        jsonOk({ id: "r_new_01" }),
      ),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });

    // List loads; click the template row to set selectedId and
    // mount the detail query. The template card is rendered as a
    // <button> (see rules/page.tsx:124) — its accessible name is the
    // template name + the rule/vendor counts. Match on the name.
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: /custom coi template/i }),
      ).toBeInTheDocument(),
    );
    expect(listCalls).toBe(1);

    fireEvent.click(
      screen.getByRole("button", { name: /custom coi template/i }),
    );

    // Detail mounts and resolves — the description (visible only on
    // the detail panel, not the list-side card) confirms detail
    // fetch landed. The list still shows "User-editable COI
    // checklist" too (it's the description on the summary), so
    // assert via the detail-panel HEADING ("Custom COI Template" as
    // an <h2>) which only renders inside the detail section.
    await waitFor(() =>
      expect(
        screen.getByRole("heading", { name: /custom coi template/i }),
      ).toBeInTheDocument(),
    );
    expect(detailCalls).toBe(1);

    // Fill the NewRuleRow's required fields (fieldName + operator).
    // The Add button is disabled until both are non-empty (page.tsx:
    // 263). Field placeholders match the page's <Input> shapes.
    fireEvent.change(screen.getByPlaceholderText(/general_liability_limit/i), {
      target: { value: "general_liability_limit" },
    });
    // The operator <select> defaults to "required" so we leave it.

    fireEvent.click(screen.getByRole("button", { name: /^add$/i }));

    // Critical assertion: AFTER onSuccess invalidates ['templates'],
    // the detail observer refetches exactly ONCE — total detailCalls
    // === 2 (initial mount + one invalidate-driven refetch).
    // A regression that re-adds the explicit
    // invalidateQueries(['templates', selectedId]) on top of the
    // broader invalidate would push detailCalls to 3 and fail this
    // assertion. (#93 / #81)
    await waitFor(() => {
      expect(detailCalls).toBe(2);
      expect(listCalls).toBe(2);
    });
  });

  it("deleteRule.onSuccess fires exactly ONE detail refetch (not two — #93 regression pin)", async () => {
    // Symmetric with the upsertRule pin above. The delete path used
    // the same double-invalidate anti-pattern; same exact-2 contract.
    let detailCalls = 0;
    let listCalls = 0;
    server.use(
      http.get(url("/api/compliance/templates"), () => {
        listCalls++;
        return jsonOk([EDITABLE_TEMPLATE]);
      }),
      http.get(url("/api/compliance/templates/:id"), () => {
        detailCalls++;
        return jsonOk(TEMPLATE_DETAIL_INITIAL);
      }),
      http.delete(
        url("/api/compliance/templates/:id/rules/:ruleId"),
        () => new Response(null, { status: 204 }),
      ),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: /custom coi template/i }),
      ).toBeInTheDocument(),
    );
    fireEvent.click(
      screen.getByRole("button", { name: /custom coi template/i }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("heading", { name: /custom coi template/i }),
      ).toBeInTheDocument(),
    );
    expect(detailCalls).toBe(1);
    expect(listCalls).toBe(1);

    // The rule row renders a Trash2 icon-only Button — its
    // accessible name comes from the icon's text-content
    // (lucide-react ships SVGs with no aria-label). The button has
    // no name, so query the row by its rule-text content first,
    // then click the only button within it. The page renders one
    // ghost button per rule row, with no name attribute, so we
    // query all buttons and pick the trash-icon one by its position
    // relative to the rule's field-name cell. Simpler approach:
    // there's only one user-editable rule visible (the system-
    // template rules don't render the delete button per page.tsx:
    // 194), so the only ghost-style trash button is THE rule
    // delete. Query by role + name being empty, take the only
    // candidate inside the rule row.
    //
    // Practical impl: the rule row contains the fieldName as a
    // table cell, then the trash button. Scope to the row via
    // closest('tr') from the field-name text.
    const fieldNameCell = screen.getByText("policy_number");
    const row = fieldNameCell.closest("tr");
    expect(row).not.toBeNull();
    const deleteBtn = row!.querySelector("button");
    expect(deleteBtn).not.toBeNull();
    fireEvent.click(deleteBtn!);

    // After onSuccess, the detail observer refetches exactly once.
    await waitFor(() => {
      expect(detailCalls).toBe(2);
      expect(listCalls).toBe(2);
    });
  });
});
