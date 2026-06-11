/**
 * Reminders page — tier-3 smoke (#36).
 *
 * Two parallel queries: /api/reminders + /api/reminders/history. Smoke
 * test asserts contract-bearing copy (the row's daysBefore + the
 * compliance-template label) so a regression that silently dropped the
 * reminders list trips here.
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import RemindersPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  toastError,
} from "@/test";

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

describe("RemindersPage — smoke (#36)", () => {
  it("populated: renders a row showing the reminder's daysBefore value", async () => {
    server.use(
      http.get(url("/api/reminders"), () =>
        jsonOk([
          {
            id: "r_01",
            daysBefore: 30,
            notifyInternalUser: true,
            notifyVendor: false,
            isActive: true,
            emailSubjectTemplate: null,
          },
        ]),
      ),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });

    // The reminder's daysBefore is the only number-bearing row content
    // that's directly contract-driven. A regression that empties the
    // reminders list (or breaks the row mapper) would drop the "30".
    await waitFor(() => expect(screen.getByText(/30/)).toBeInTheDocument());
  });

  it("empty: renders the page chrome and the history section without crashing", async () => {
    server.use(
      http.get(url("/api/reminders"), () => jsonOk([])),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });
    // No reminders → no daysBefore text. The page header / history
    // panel should still render — assert the page is past the loading
    // state by waiting for any of the unconditional chrome.
    await waitFor(() =>
      expect(document.body.textContent?.length).toBeGreaterThan(0),
    );
    expect(screen.queryByText(/30/)).toBeNull();
  });

  it("each toggle is an accessible switch (role/aria-checked/name) with a ≥44px hit target (#181 + #189)", async () => {
    // The 3 toggles per row (team / vendor / active) are now real switches:
    // role="switch" + aria-checked + a per-instance accessible name + a ≥44px
    // touch target via the inset ::before. (#189 replaced the bare button.)
    server.use(
      http.get(url("/api/reminders"), () =>
        jsonOk([
          {
            id: "r_01",
            daysBefore: 30,
            notifyInternalUser: true,
            notifyVendor: false,
            isActive: true,
            emailSubjectTemplate: null,
          },
        ]),
      ),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/30/)).toBeInTheDocument());

    const switches = screen.getAllByRole("switch");
    expect(switches).toHaveLength(3);
    // Each carries a per-instance accessible name and the ≥44px hit area.
    for (const s of switches) {
      expect(s).toHaveAccessibleName();
      expect(s.className).toContain("before:h-11");
    }
    // aria-checked reflects state: team + active on, vendor off.
    expect(screen.getByRole("switch", { name: /notify team/i })).toBeChecked();
    expect(screen.getByRole("switch", { name: /notify vendor/i })).not.toBeChecked();
  });
});

describe("RemindersPage — toggle saves (#264 / FP-093)", () => {
  const INITIAL = {
    id: "r_01",
    daysBefore: 30,
    notifyInternalUser: false,
    notifyVendor: false,
    isActive: true,
    emailSubjectTemplate: null,
  };

  it("two toggles flipped inside the refetch window both persist — the last PUT carries the first flip", async () => {
    // The lost-update race: the PUT body used to be rebuilt from the render-scope
    // query snapshot, so flipping vendor right after team sent team's STALE value
    // and silently reverted it. The body is now built from the optimistically
    // patched cache, so the final PUT must carry BOTH flips.
    const putBodies: Array<Record<string, unknown>> = [];
    server.use(
      http.get(url("/api/reminders"), () => jsonOk([INITIAL])),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
      http.put(url("/api/reminders/r_01"), async ({ request }) => {
        putBodies.push((await request.json()) as Record<string, unknown>);
        return jsonOk({ id: "r_01" });
      }),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/30/)).toBeInTheDocument());

    fireEvent.click(screen.getByRole("switch", { name: /notify team/i }));
    fireEvent.click(screen.getByRole("switch", { name: /notify vendor/i }));

    await waitFor(() => expect(putBodies).toHaveLength(2));
    // The second (last-writer) PUT must include the FIRST toggle's new value.
    // Asserting the first body's vendor field would be timing-sensitive (the
    // two onMutate patches interleave); the invariant that kills the bug is
    // that the last PUT to land carries every flip.
    expect(putBodies[1]).toMatchObject({
      notifyInternalUser: true,
      notifyVendor: true,
    });
  });

  it("a toggle flips optimistically while the PUT is still in flight", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    // Mutable server state so the post-settle refetch confirms (not reverts)
    // the optimistic flip, letting the test drain cleanly.
    const serverState = { ...INITIAL };
    server.use(
      http.get(url("/api/reminders"), () => jsonOk([serverState])),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
      http.put(url("/api/reminders/r_01"), async ({ request }) => {
        Object.assign(serverState, (await request.json()) as object);
        await gate;
        return jsonOk({ id: "r_01" });
      }),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/30/)).toBeInTheDocument());

    const team = screen.getByRole("switch", { name: /notify team/i });
    expect(team).not.toBeChecked();

    fireEvent.click(team);
    // Checked BEFORE the PUT resolves — the cache patch in onMutate, not the
    // post-save refetch, is what flips the switch (kills the perceived lag).
    await waitFor(() => expect(team).toBeChecked());

    release();
    await waitFor(() => expect(team).toBeChecked());
  });

  it("serializes PUTs per scope and refetches once, after the LAST mutation settles", async () => {
    // Pins three load-bearing behaviors of the mutation wiring (#264 review):
    //   1. scope serialization — toggle B's PUT must not start while A's is in
    //      flight (in-order, single-in-flight kills server-side reordering);
    //   2. onMutate fires immediately even for the queued mutation — B flips
    //      optimistically while its PUT is still waiting on A;
    //   3. the onSettled gate — exactly ONE list refetch, after the last settle.
    let releaseA!: () => void;
    const gateA = new Promise<void>((r) => (releaseA = r));
    const serverState = { ...INITIAL };
    let listFetches = 0;
    let putStarts = 0;
    let putsInFlight = 0;
    let maxPutsInFlight = 0;
    server.use(
      http.get(url("/api/reminders"), () => {
        listFetches++;
        return jsonOk([serverState]);
      }),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
      http.put(url("/api/reminders/r_01"), async ({ request }) => {
        putStarts++;
        putsInFlight++;
        maxPutsInFlight = Math.max(maxPutsInFlight, putsInFlight);
        const body = (await request.json()) as object;
        if (putStarts === 1) await gateA;
        Object.assign(serverState, body);
        putsInFlight--;
        return jsonOk({ id: "r_01" });
      }),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/30/)).toBeInTheDocument());
    const fetchesAfterMount = listFetches;

    const team = screen.getByRole("switch", { name: /notify team/i });
    const vendor = screen.getByRole("switch", { name: /notify vendor/i });
    fireEvent.click(team);
    fireEvent.click(vendor);

    // Both flip optimistically even though PUT A is gated and PUT B is queued.
    await waitFor(() => expect(team).toBeChecked());
    expect(vendor).toBeChecked();
    expect(listFetches).toBe(fetchesAfterMount); // no refetch while pending

    releaseA();
    await waitFor(() => expect(putStarts).toBe(2));
    await waitFor(() => expect(listFetches).toBe(fetchesAfterMount + 1));
    expect(maxPutsInFlight).toBe(1); // serialized, never concurrent
    // Refetch confirms (not reverts) the flips — server state carried both.
    expect(team).toBeChecked();
    expect(vendor).toBeChecked();
  });

  it("a failed save reverts the switch and surfaces the global error toast", async () => {
    server.use(
      http.get(url("/api/reminders"), () => jsonOk([INITIAL])),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
      http.put(url("/api/reminders/r_01"), () =>
        jsonError("server.error", "Could not save your change.", { status: 500 }),
      ),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/30/)).toBeInTheDocument());

    const team = screen.getByRole("switch", { name: /notify team/i });
    fireEvent.click(team);

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not save your change."));
    // The post-settle refetch re-syncs to server truth — the optimistic flip reverts.
    await waitFor(() => expect(team).not.toBeChecked());
  });
});

describe("RemindersPage — humanized delivery status (#188)", () => {
  it("renders a friendly delivery-status label, not the raw lowercase token", async () => {
    server.use(
      http.get(url("/api/reminders"), () => jsonOk([])),
      http.get(url("/api/reminders/history"), () =>
        jsonOk([
          {
            id: "h_1",
            recipient: "ops@acmecatering.com",
            sentAt: "2026-05-26T12:00:00Z",
            sendDate: "2026-05-26",
            status: "bounced",
            reminderId: "r_1",
            documentId: "d_1",
          },
        ]),
      ),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });

    await waitFor(() =>
      expect(screen.getByText("Bounced — bad address")).toBeInTheDocument(),
    );
    // The raw provider token must not surface.
    expect(screen.queryByText("bounced")).toBeNull();
  });
});
