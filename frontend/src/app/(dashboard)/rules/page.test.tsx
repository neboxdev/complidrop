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
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import RulesPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  toastSuccess,
  toastError,
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

describe("RulesPage — create-template failure surfaces a toast (no more silent failure)", () => {
  it("a failed POST /api/compliance/templates fires toast.error with the server message, not HTTP jargon", async () => {
    // Regression pin for the reported prod bug: clicking create when the
    // request failed used to do NOTHING — createTemplate had no onError and
    // there was no global handler, so the rejection was swallowed. With the
    // global mutation-error net (createTemplate now carries
    // `meta: { errorToast: true }`, handled in lib/query-client.ts), a
    // failure MUST surface a jargon-free toast.
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.post(url("/api/compliance/templates"), () =>
        jsonError("server.error", "Could not create template.", { status: 500 }),
      ),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });

    const input = await screen.findByPlaceholderText(/template name/i);
    fireEvent.change(input, { target: { value: "My COI checklist" } });

    const createBtn = screen.getByRole("button", { name: /create template/i });
    expect(createBtn).not.toBeDisabled();
    fireEvent.click(createBtn);

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith("Could not create template."),
    );

    // CLAUDE.md frontend error-message policy: never leak HTTP jargon /
    // browser TypeErrors / raw status codes into toast copy.
    const shown = String(toastError.mock.calls.at(-1)?.[0] ?? "");
    expect(shown).not.toMatch(/bad gateway|failed to fetch|typeerror|\b50\d\b/i);

    // The success toast must NOT have fired on a failed create.
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});

describe("RulesPage — rule mutations prefix-invalidate ['templates'] (#93)", () => {
  it("upsertRule.onSuccess fires exactly ONE detail refetch (not two — #93 regression pin) AND surfaces the 'Rule saved' toast", async () => {
    // Mirrors the useUpdateVendor exact-2 pattern from #81 at the
    // page layer. The original code's `invalidateQueries(['templates',
    // selectedId])` + `invalidateQueries(['templates'])` combo
    // double-fired the detail refetch per save. With the fix
    // (broader-only invalidate, prefix-match covers detail), the
    // detail observer should refetch exactly once after Add → onSuccess.
    //
    // Bidirectional contract:
    //   - detailCalls=2 catches the FORWARD regression (re-adding the
    //     narrow ['templates', selectedId] invalidate on top would
    //     push the count to 3).
    //   - listCalls=2 catches the BACKWARD regression (replacing the
    //     broader invalidate with just ['templates', selectedId]
    //     would leave the list stale at listCalls=1). Mirrors the
    //     useVendors.test.tsx structural cross-check from #81.
    //
    // Mutation request body is captured so a regression that breaks
    // the mutation invocation entirely (e.g. dropping the
    // upsertRule.mutate(...) call from NewRuleRow.onSave) surfaces
    // as a "mutation never fired" assertion failure rather than an
    // ambiguous detailCalls-timeout. Same pattern as
    // login/page.test.tsx — observable-settlement on the mutation
    // before teardown. (#93 review — test-quality reviewer.)
    let detailCalls = 0;
    let listCalls = 0;
    let upsertBody: unknown;
    server.use(
      http.get(url("/api/compliance/templates"), () => {
        listCalls++;
        return jsonOk([EDITABLE_TEMPLATE]);
      }),
      http.get(url("/api/compliance/templates/:id"), () => {
        detailCalls++;
        return jsonOk(TEMPLATE_DETAIL_INITIAL);
      }),
      http.post(
        url("/api/compliance/templates/:id/rules"),
        async ({ request }) => {
          upsertBody = await request.json();
          return jsonOk({ id: "r_new_01" });
        },
      ),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });

    // List loads; click the template row to set selectedId and
    // mount the detail query. The template card is rendered as a
    // <button> (see rules/page.tsx:124).
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: /custom coi template/i }),
      ).toBeInTheDocument(),
    );
    expect(listCalls).toBe(1);

    fireEvent.click(
      screen.getByRole("button", { name: /custom coi template/i }),
    );

    // Detail mounts and resolves — assert via the detail-panel
    // <h2> which only renders inside the detail section once
    // detail.data lands. Co-pin cardinality so a future redesign
    // that promotes the list-card name into a heading (giving the
    // same accessible name twice) fails this test loudly. (#93
    // review — test-quality reviewer.)
    await waitFor(() =>
      expect(
        screen.getByRole("heading", { name: /custom coi template/i }),
      ).toBeInTheDocument(),
    );
    expect(
      screen.getAllByRole("heading", { name: /custom coi template/i }),
    ).toHaveLength(1);
    expect(detailCalls).toBe(1);

    // Fill the NewRuleRow's required fields. The Add button is
    // disabled until `fieldName && operator` are both truthy
    // (page.tsx:263). The operator <select> defaults to "required"
    // (useState init at page.tsx:225) — assert the Add button is
    // NOT disabled BEFORE clicking so a future change to the
    // operator default would surface as an explicit precondition
    // failure rather than an ambiguous "click did nothing"
    // timeout. (#93 review — test-quality reviewer.)
    fireEvent.change(
      screen.getByPlaceholderText(/general_liability_limit/i),
      {
        target: { value: "general_liability_limit" },
      },
    );
    const addBtn = screen.getByRole("button", { name: /^add$/i });
    expect(addBtn).not.toBeDisabled();
    fireEvent.click(addBtn);

    // Critical assertion: AFTER onSuccess invalidates ['templates'],
    // the detail observer refetches exactly ONCE — total detailCalls
    // === 2 (initial mount + one invalidate-driven refetch).
    await waitFor(() => {
      expect(detailCalls).toBe(2);
      expect(listCalls).toBe(2);
    });

    // toast.success('Rule saved') is part of upsertRule.onSuccess
    // (page.tsx:84). A regression that drops the toast call leaves
    // the user with no feedback that the save landed — pin the
    // string match so the silent-save regression is caught. Also
    // doubles as observable-settlement of the mutation before
    // teardown. (#93 review — test-quality reviewer.)
    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Rule saved"),
    );

    // The mutation actually fired with the expected payload —
    // converts ambiguous "detailCalls never reached 2" timeouts
    // into a "mutation never invoked" diagnostic.
    expect(upsertBody).toMatchObject({
      fieldName: "general_liability_limit",
      operator: "required",
    });
  });

  it("deleteRule.onSuccess fires exactly ONE detail refetch (not two — #93 regression pin); deliberately does NOT toast", async () => {
    // Symmetric with the upsertRule pin above. The delete path used
    // the same double-invalidate anti-pattern; same exact-2 contract.
    //
    // ASYMMETRY NOTE: deleteRule.onSuccess does NOT fire a
    // toast.success (page.tsx:97 omits it), unlike upsertRule which
    // toasts "Rule saved". Pin that absence here so a future
    // contributor who "fixes the missing toast" without coordinating
    // with the asymmetry sees a failing test that prompts the
    // discussion. If the team later decides delete SHOULD toast,
    // this assertion is the signal to update both the page AND
    // this test together.
    let detailCalls = 0;
    let listCalls = 0;
    let deleteRuleIdSeen: string | undefined;
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
        ({ params }) => {
          deleteRuleIdSeen = params.ruleId as string;
          return new Response(null, { status: 204 });
        },
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

    // Find the delete button via its accessible name (added in #93's
    // review: `aria-label="Delete rule"` on the icon-only Button in
    // page.tsx). Scope to the row via the rule's field-name cell so
    // a future page that gains multiple delete buttons (e.g. a
    // separate row of system-template stub rules) still resolves
    // unambiguously. (#93 review — test-quality reviewer flagged
    // the previous closest+querySelector chain as fragile.)
    const ruleRow = within(screen.getByRole("table"))
      .getByText("Policy number") // humanized fieldName (#188)
      .closest("tr");
    expect(ruleRow).not.toBeNull();
    const deleteBtn = within(ruleRow as HTMLElement).getByRole("button", {
      name: /delete rule/i,
    });
    fireEvent.click(deleteBtn);

    // After onSuccess, the detail observer refetches exactly once.
    // Same bidirectional contract as the upsertRule test:
    // detailCalls=2 catches double-invalidate regressions,
    // listCalls=2 catches narrow-only regressions.
    await waitFor(() => {
      expect(detailCalls).toBe(2);
      expect(listCalls).toBe(2);
    });

    // The mutation fired with the expected rule id (observable
    // settlement + converts a mutation-never-invoked regression
    // into a clear diagnostic).
    expect(deleteRuleIdSeen).toBe("r_existing_01");

    // Deliberate no-toast asymmetry pin — see the describe-block
    // comment above. A future "fix" that adds toast.success to
    // deleteRule.onSuccess MUST update this assertion in the same
    // PR, forcing the deliberate-asymmetry conversation.
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});

describe("RulesPage — humanized rule display (#188)", () => {
  it("renders the doc type, field, and operator as friendly labels, not raw codes", async () => {
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([EDITABLE_TEMPLATE])),
      http.get(url("/api/compliance/templates/:id"), () =>
        jsonOk({
          ...TEMPLATE_DETAIL_INITIAL,
          rules: [
            {
              id: "r_gll",
              documentType: "coi",
              fieldName: "general_liability_limit",
              operator: "min_value",
              expectedValue: "1000000",
              errorMessage: "GL must be at least $1M",
              sortOrder: 1,
            },
          ],
        }),
      ),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(
      await screen.findByRole("button", { name: /custom coi template/i }),
    );

    // Scope to the existing rule's row — the NewRuleRow add-form <select>
    // options also render humanized doc-type/operator labels, so a bare
    // getByText would be ambiguous.
    const fieldCell = await screen.findByText("General liability limit");
    const row = fieldCell.closest("tr") as HTMLElement;
    expect(within(row).getByText("Certificate of Insurance")).toBeInTheDocument();
    expect(within(row).getByText("Must be at least")).toBeInTheDocument();
    // None of the raw machine codes leak into the requirements table.
    expect(screen.queryByText("general_liability_limit")).toBeNull();
    expect(screen.queryByText("min_value")).toBeNull();
    expect(within(row).queryByText("coi")).toBeNull();
  });
});
