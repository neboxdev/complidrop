/**
 * Vendor requirements page (rebuilt in #192) — plain-English checklist authoring.
 *
 * Covers:
 *   - Smoke: list of checklists + empty "your checklists" state + create input.
 *   - Plain-English authoring: "+ Add a requirement" → pick a sentence → money
 *     input → POSTs the correct engine rule shape (the user never types a field
 *     name, operator, or unformatted number).
 *   - #93 cache-invalidation contract carried over: upsert + delete each fire
 *     EXACTLY ONE detail refetch (prefix-invalidate, never the old double-fire).
 *   - Clone a suggested checklist ("Use this") via frontend rule-replay.
 *   - No raw machine codes leak; live compliance summary; delete-checklist confirm.
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

const EDITABLE = {
  id: "t_user_01",
  name: "Caterer",
  description: "Editable caterer checklist",
  isSystemTemplate: false,
  ruleCount: 1,
  vendorCount: 2,
};

const DETAIL = {
  id: "t_user_01",
  name: "Caterer",
  description: "Editable caterer checklist",
  isSystemTemplate: false,
  rules: [
    {
      id: "r_gl_01",
      documentType: "coi",
      fieldName: "general_liability_limit",
      operator: "min_value",
      expectedValue: "1000000",
      errorMessage: "GL too low",
      sortOrder: 1,
    },
  ],
};

const SUGGESTED = {
  id: "t_sys_01",
  name: "Photographer / Videographer",
  description: "Suggested",
  isSystemTemplate: true,
  ruleCount: 3,
  vendorCount: 0,
};

describe("RulesPage — assigned-template seam (#239)", () => {
  it("acknowledges an in-use suggested checklist instead of 'None yet'", async () => {
    const inUse = {
      id: "t_sys_caterer",
      name: "Caterer",
      description: "Suggested",
      isSystemTemplate: true,
      ruleCount: 3,
      vendorCount: 1,
    };
    server.use(http.get(url("/api/compliance/templates"), () => jsonOk([inUse])));
    renderWithProviders(<RulesPage />, { auth: authedMe });

    // The #237 seam: "Your checklists — None yet" while a system checklist is assigned. Healed.
    expect(await screen.findByText(/you.re using a suggested checklist/i)).toBeInTheDocument();
    expect(screen.queryByText(/none yet/i)).toBeNull();

    // The assigned suggested checklist is now visibly "In use" with its vendor usage.
    expect(screen.getByText(/^in use$/i)).toBeInTheDocument();
    expect(screen.getByText(/used by 1 vendor/i)).toBeInTheDocument();
  });

  it("keeps 'None yet' when no suggested checklist is in use", async () => {
    server.use(http.get(url("/api/compliance/templates"), () => jsonOk([SUGGESTED])));
    renderWithProviders(<RulesPage />, { auth: authedMe });

    expect(await screen.findByText(/none yet/i)).toBeInTheDocument();
    expect(screen.queryByText(/^in use$/i)).toBeNull();
  });
});

describe("RulesPage — smoke + reframe (#192)", () => {
  it("renders the 'Vendor requirements' header and lists suggested checklists", async () => {
    server.use(http.get(url("/api/compliance/templates"), () => jsonOk([SUGGESTED])));
    renderWithProviders(<RulesPage />, { auth: authedMe });

    expect(screen.getByRole("heading", { name: /vendor requirements/i })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText("Photographer / Videographer")).toBeInTheDocument());
    // The new-checklist input is wired to its label (gap #21 / #76).
    expect(screen.getByLabelText(/new checklist/i)).toBeInstanceOf(HTMLInputElement);
  });

  it("create-checklist failure surfaces a jargon-free toast", async () => {
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.post(url("/api/compliance/templates"), () =>
        jsonError("server.error", "Could not create checklist.", { status: 500 }),
      ),
    );
    renderWithProviders(<RulesPage />, { auth: authedMe });

    fireEvent.change(screen.getByLabelText(/new checklist/i), { target: { value: "Caterer" } });
    fireEvent.click(screen.getByRole("button", { name: /create checklist/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not create checklist."));
    const shown = String(toastError.mock.calls.at(-1)?.[0] ?? "");
    expect(shown).not.toMatch(/bad gateway|failed to fetch|typeerror|\b50\d\b/i);
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});

describe("RulesPage — plain-English authoring (#192)", () => {
  it("adds a requirement by picking a sentence + money preset, POSTing the engine rule shape", async () => {
    let detailCalls = 0;
    let listCalls = 0;
    let body: Record<string, unknown> | undefined;
    server.use(
      http.get(url("/api/compliance/templates"), () => {
        listCalls++;
        return jsonOk([EDITABLE]);
      }),
      http.get(url("/api/compliance/templates/:id"), () => {
        detailCalls++;
        return jsonOk({ ...DETAIL, rules: [] });
      }),
      http.post(url("/api/compliance/templates/:id/rules"), async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return jsonOk({ id: "r_new" });
      }),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /caterer/i }));
    await screen.findByRole("heading", { name: /caterer/i });
    expect(detailCalls).toBe(1);

    // Open the menu, pick "General liability", choose the $1,000,000 preset, Add.
    fireEvent.click(screen.getByRole("button", { name: /add a requirement/i }));
    fireEvent.click(screen.getByRole("button", { name: /general liability/i }));
    fireEvent.click(screen.getByRole("button", { name: "$1,000,000" }));
    fireEvent.click(screen.getByRole("button", { name: /^add$/i }));

    // The mapped rule went out with the engine tokens the user never typed.
    await waitFor(() => expect(body).toBeDefined());
    expect(body).toMatchObject({
      documentType: "coi",
      fieldName: "general_liability_limit",
      operator: "min_value",
      expectedValue: "1000000",
    });
    expect(String(body!.errorMessage)).toMatch(/\$1,000,000/);

    // #93 contract: exactly ONE detail refetch + ONE list refetch after onSuccess.
    await waitFor(() => {
      expect(detailCalls).toBe(2);
      expect(listCalls).toBe(2);
    });
  });

  it("the 'not expired' toggle POSTs the honest required-on-expiration_date rule (no value)", async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([EDITABLE])),
      http.get(url("/api/compliance/templates/:id"), () => jsonOk({ ...DETAIL, rules: [] })),
      http.post(url("/api/compliance/templates/:id/rules"), async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return jsonOk({ id: "r_exp" });
      }),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /caterer/i }));
    await screen.findByRole("heading", { name: /caterer/i });

    fireEvent.click(screen.getByRole("button", { name: /add a requirement/i }));
    fireEvent.click(screen.getByRole("button", { name: /document must not be expired/i }));
    // valueKind "none" → Add is immediately enabled (no fill-in).
    fireEvent.click(screen.getByRole("button", { name: /^add$/i }));

    await waitFor(() => expect(body).toBeDefined());
    expect(body).toMatchObject({ documentType: "coi", fieldName: "expiration_date", operator: "required" });
    expect(body!.expectedValue).toBeNull();
  });

  it("a text requirement (additional insured) POSTs the typed value as a contains rule", async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([EDITABLE])),
      http.get(url("/api/compliance/templates/:id"), () => jsonOk({ ...DETAIL, rules: [] })),
      http.post(url("/api/compliance/templates/:id/rules"), async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return jsonOk({ id: "r_ai" });
      }),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /caterer/i }));
    await screen.findByRole("heading", { name: /caterer/i });

    fireEvent.click(screen.getByRole("button", { name: /add a requirement/i }));
    fireEvent.click(screen.getByRole("button", { name: /names you as additional insured/i }));
    fireEvent.change(screen.getByLabelText(/name to look for/i), {
      target: { value: "Riverside Event Hall" },
    });
    fireEvent.click(screen.getByRole("button", { name: /^add$/i }));

    await waitFor(() => expect(body).toBeDefined());
    expect(body).toMatchObject({
      documentType: "coi",
      fieldName: "additional_insured",
      operator: "contains",
      expectedValue: "Riverside Event Hall",
    });
  });

  it("editing a money requirement pre-fills the formatted amount and upserts with the existing rule id", async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([EDITABLE])),
      http.get(url("/api/compliance/templates/:id"), () => jsonOk(DETAIL)), // has r_gl_01 @ $1,000,000
      http.post(url("/api/compliance/templates/:id/rules"), async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return jsonOk({ id: "r_gl_01" });
      }),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /caterer/i }));
    fireEvent.click(await screen.findByRole("button", { name: /edit requirement/i }));

    // The stored bare integer "1000000" pre-fills as the formatted display.
    const amount = screen.getByLabelText(/minimum coverage amount/i) as HTMLInputElement;
    expect(amount.value).toBe("$1,000,000");
    fireEvent.change(amount, { target: { value: "2000000" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(body).toBeDefined());
    // Upsert-with-id (an EDIT, not a duplicate insert), new amount stored as bare integer.
    expect(body).toMatchObject({
      id: "r_gl_01",
      fieldName: "general_liability_limit",
      operator: "min_value",
      expectedValue: "2000000",
    });
  });

  it("the money field uses a numeric keyboard and disables Add until an amount is entered (gap #21)", async () => {
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([EDITABLE])),
      http.get(url("/api/compliance/templates/:id"), () => jsonOk({ ...DETAIL, rules: [] })),
    );
    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /caterer/i }));
    await screen.findByRole("heading", { name: /caterer/i });

    fireEvent.click(screen.getByRole("button", { name: /add a requirement/i }));
    fireEvent.click(screen.getByRole("button", { name: /general liability/i }));

    // Add is disabled with a stated reason until a value is entered.
    expect(screen.getByRole("button", { name: /^add$/i })).toBeDisabled();
    expect(screen.getByText(/enter a coverage amount/i)).toBeInTheDocument();

    const amount = screen.getByLabelText(/minimum coverage amount/i) as HTMLInputElement;
    expect(amount.inputMode).toBe("numeric");
    fireEvent.change(amount, { target: { value: "2000000" } });
    expect(screen.getByRole("button", { name: /^add$/i })).not.toBeDisabled();
  });

  it("renders requirements as sentences with no raw machine codes, plus a live summary", async () => {
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([EDITABLE])),
      http.get(url("/api/compliance/templates/:id"), () => jsonOk(DETAIL)),
    );
    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /caterer/i }));

    // The sentence appears in BOTH the requirement row and the live summary.
    expect(
      (await screen.findAllByText(/carries at least \$1,000,000 in general liability insurance/i)).length,
    ).toBeGreaterThanOrEqual(2);
    // Live summary reads back the checklist, grouped per document type (#319 FP-083).
    expect(screen.getByText(/for a caterer to be covered/i)).toBeInTheDocument();
    expect(screen.getByText(/each certificate of insurance must prove/i)).toBeInTheDocument();
    // No raw tokens leak.
    expect(screen.queryByText("general_liability_limit")).toBeNull();
    expect(screen.queryByText("min_value")).toBeNull();
  });
});

describe("RulesPage — delete requirement fires one refetch (#93 carried over)", () => {
  it("removing a requirement deletes it and refetches the detail exactly once", async () => {
    let detailCalls = 0;
    let listCalls = 0;
    let deletedId: string | undefined;
    server.use(
      http.get(url("/api/compliance/templates"), () => {
        listCalls++;
        return jsonOk([EDITABLE]);
      }),
      http.get(url("/api/compliance/templates/:id"), () => {
        detailCalls++;
        return jsonOk(DETAIL);
      }),
      http.delete(url("/api/compliance/templates/:id/rules/:ruleId"), ({ params }) => {
        deletedId = params.ruleId as string;
        return new Response(null, { status: 204 });
      }),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /caterer/i }));
    const removeBtn = await screen.findByRole("button", { name: /remove requirement/i });
    expect(detailCalls).toBe(1);

    fireEvent.click(removeBtn);

    await waitFor(() => {
      expect(detailCalls).toBe(2);
      expect(listCalls).toBe(2);
    });
    expect(deletedId).toBe("r_gl_01");
  });
});

describe("RulesPage — suggested checklists clone (#192)", () => {
  // Rules are listed OUT of sortOrder (sortOrder-2 first) on purpose, so the
  // "in order" assertion below actually pins the cloneChecklist sortOrder sort —
  // a dropped .sort() would replay them in array order and fail.
  const TWO_RULE_SUGGESTED = {
    id: "t_sys_01",
    name: "Photographer / Videographer",
    description: "Suggested",
    isSystemTemplate: true,
    rules: [
      { id: "s2", documentType: "coi", fieldName: "expiration_date", operator: "required", expectedValue: null, errorMessage: "y", sortOrder: 2 },
      { id: "s1", documentType: "coi", fieldName: "general_liability_limit", operator: "min_value", expectedValue: "500000", errorMessage: "x", sortOrder: 1 },
    ],
  };

  it("'Use this' replays EVERY suggested rule in order into a new editable checklist and selects it", async () => {
    let created = 0;
    const replayed: string[] = [];
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([SUGGESTED])),
      http.get(url("/api/compliance/templates/t_sys_01"), () => jsonOk(TWO_RULE_SUGGESTED)),
      http.post(url("/api/compliance/templates"), () => {
        created++;
        return jsonOk({ id: "t_clone_01" });
      }),
      http.post(url("/api/compliance/templates/t_clone_01/rules"), async ({ request }) => {
        const b = (await request.json()) as { fieldName: string };
        replayed.push(b.fieldName);
        return jsonOk({ id: "r_clone" });
      }),
      http.get(url("/api/compliance/templates/t_clone_01"), () =>
        jsonOk({ ...DETAIL, id: "t_clone_01", isSystemTemplate: false }),
      ),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /use this/i }));

    await waitFor(() => {
      expect(created).toBe(1);
      // BOTH rules replayed, in sortOrder.
      expect(replayed).toEqual(["general_liability_limit", "expiration_date"]);
    });
    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Checklist added — edit it to fit your vendors"),
    );
  });

  it("rolls back the new checklist (and surfaces a friendly toast) if a rule fails mid-replay", async () => {
    let ruleCalls = 0;
    let rolledBack = false;
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([SUGGESTED])),
      http.get(url("/api/compliance/templates/t_sys_01"), () => jsonOk(TWO_RULE_SUGGESTED)),
      http.post(url("/api/compliance/templates"), () => jsonOk({ id: "t_clone_01" })),
      http.post(url("/api/compliance/templates/t_clone_01/rules"), () => {
        ruleCalls++;
        // First rule succeeds, second fails mid-replay.
        return ruleCalls === 1
          ? jsonOk({ id: "r_clone" })
          : jsonError("server.error", "Couldn't copy a requirement.", { status: 500 });
      }),
      http.delete(url("/api/compliance/templates/t_clone_01"), () => {
        rolledBack = true;
        return new Response(null, { status: 204 });
      }),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /use this/i }));

    // The half-created checklist is deleted (no orphan)...
    await waitFor(() => expect(rolledBack).toBe(true));
    // ...and the failure is surfaced jargon-free, not as a silent success.
    await waitFor(() => expect(toastError).toHaveBeenCalled());
    const msg = String(toastError.mock.calls.at(-1)?.[0] ?? "");
    expect(msg).not.toMatch(/typeerror|failed to fetch|\b50\d\b/i);
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});

describe("RulesPage — delete-checklist confirm dialog (#189 carried over)", () => {
  it("opens an accessible confirm dialog; deletes on confirm, not on cancel", async () => {
    let deleteCalls = 0;
    server.use(
      http.get(url("/api/compliance/templates"), () => jsonOk([EDITABLE])),
      http.get(url("/api/compliance/templates/:id"), () => jsonOk(DETAIL)),
      http.delete(url("/api/compliance/templates/:id"), () => {
        deleteCalls++;
        return new Response(null, { status: 204 });
      }),
    );

    renderWithProviders(<RulesPage />, { auth: authedMe });
    fireEvent.click(await screen.findByRole("button", { name: /caterer/i }));
    await screen.findByRole("button", { name: /delete checklist/i });

    // Cancel → no delete.
    fireEvent.click(screen.getByRole("button", { name: /delete checklist/i }));
    const dialog = await screen.findByRole("alertdialog");
    expect(dialog).toHaveAccessibleName(/delete caterer\?/i);
    fireEvent.click(within(dialog).getByRole("button", { name: /cancel/i }));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
    expect(deleteCalls).toBe(0);

    // Confirm → delete fires.
    fireEvent.click(screen.getByRole("button", { name: /delete checklist/i }));
    const dialog2 = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog2).getByRole("button", { name: /^delete$/i }));
    await waitFor(() => expect(deleteCalls).toBe(1));
  });
});
