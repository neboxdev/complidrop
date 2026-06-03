/**
 * Dashboard shell (#181) — pins the responsive-shell contract: a mobile
 * hamburger opens a nav drawer from which EVERY nav item + the org context is
 * reachable (the AC: "all nav is reachable at 390px"), the drawer closes when a
 * link is tapped, the active route is marked, and children still render.
 *
 * JSDOM applies no CSS, so the desktop `<aside>` (which is `hidden md:flex`)
 * stays in the DOM alongside the drawer — assertions about the drawer scope to
 * `getByRole("dialog")` via `within()` to stay unambiguous.
 */
import { describe, it, expect } from "vitest";
import { http, HttpResponse } from "msw";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import DashboardLayout from "./layout";
import { ME_KEY } from "@/hooks/useAuth";
import { renderWithProviders, authedMe, server, url, jsonOk, jsonError, navState } from "@/test";

const NAV_LABELS = [
  "Dashboard",
  "Documents",
  "Vendors",
  "Compliance rules",
  "Reminders",
  "Export",
  "Settings",
];

describe("DashboardLayout — responsive shell (#181)", () => {
  it("opens a mobile nav drawer with every nav item + org context reachable", async () => {
    renderWithProviders(
      <DashboardLayout>
        <div>child content</div>
      </DashboardLayout>,
      { auth: authedMe, pathname: "/dashboard" },
    );

    // Drawer is closed until the hamburger is tapped.
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(
      screen.getByRole("button", { name: /open navigation menu/i }),
    );

    const drawer = await waitFor(() => screen.getByRole("dialog"));
    for (const label of NAV_LABELS) {
      expect(
        within(drawer).getByRole("link", { name: label }),
      ).toBeInTheDocument();
    }
    // Org context (name + plan) renders in the drawer footer.
    expect(within(drawer).getByText("Acme Inc")).toBeInTheDocument();
    expect(within(drawer).getByText(/pro/i)).toBeInTheDocument();
  });

  it("closes the drawer when a nav link is tapped", async () => {
    renderWithProviders(
      <DashboardLayout>
        <div>child</div>
      </DashboardLayout>,
      { auth: authedMe, pathname: "/dashboard" },
    );

    fireEvent.click(
      screen.getByRole("button", { name: /open navigation menu/i }),
    );
    const drawer = await waitFor(() => screen.getByRole("dialog"));
    fireEvent.click(within(drawer).getByRole("link", { name: "Documents" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("marks the active route with aria-current=page", () => {
    renderWithProviders(
      <DashboardLayout>
        <div>child</div>
      </DashboardLayout>,
      { auth: authedMe, pathname: "/documents" },
    );

    // The desktop sidebar is always in the DOM (no CSS in JSDOM) and marks the
    // current route — a regression that dropped aria-current would fail here.
    const links = screen.getAllByRole("link", { name: "Documents" });
    expect(links.some((a) => a.getAttribute("aria-current") === "page")).toBe(
      true,
    );
  });

  it("renders its children", () => {
    renderWithProviders(
      <DashboardLayout>
        <div>hello child</div>
      </DashboardLayout>,
      { auth: authedMe },
    );
    expect(screen.getByText("hello child")).toBeInTheDocument();
  });

  it("closes the drawer on Escape", async () => {
    renderWithProviders(
      <DashboardLayout>
        <div>child</div>
      </DashboardLayout>,
      { auth: authedMe, pathname: "/dashboard" },
    );

    fireEvent.click(
      screen.getByRole("button", { name: /open navigation menu/i }),
    );
    const drawer = await waitFor(() => screen.getByRole("dialog"));
    fireEvent.keyDown(drawer, { key: "Escape", code: "Escape" });

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

/**
 * Session resilience (#182) — a TRANSIENT `/me` failure (backend 5xx or a
 * network blip) must NOT evict a valid session to /login, which would mask an
 * outage as an auth problem. The layout now redirects ONLY on the explicit
 * logged-out signal (`me.data === null`, set by useMe's 401→null mapping or
 * the global auth-error handler) and renders an in-shell Retry on a transient
 * error (`me.isError && me.data === undefined`).
 *
 * These tests deliberately DON'T seed `auth`, so `useMe()` exercises the real
 * fetch → api.ts → query path against MSW (the layer where the bug lived).
 */
describe("DashboardLayout — session resilience (#182)", () => {
  it("does NOT redirect on a transient /me 500; shows an in-shell Retry", async () => {
    server.use(
      http.get(url("/api/auth/me"), () =>
        jsonError("server.error", "Something went wrong. Try again.", { status: 500 }),
      ),
    );

    renderWithProviders(
      <DashboardLayout>
        <div>protected child</div>
      </DashboardLayout>,
    );

    // In-shell retry surfaces instead of a redirect.
    await waitFor(() =>
      expect(screen.getByText(/couldn't reach the server/i)).toBeInTheDocument(),
    );
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
    // The protected child is gated, but the user is NOT bounced to /login.
    expect(screen.queryByText("protected child")).toBeNull();
    expect(navState.router.replace).not.toHaveBeenCalled();
  });

  it("does NOT redirect when fetch() itself fails (offline / network blip)", async () => {
    server.use(http.get(url("/api/auth/me"), () => HttpResponse.error()));

    renderWithProviders(
      <DashboardLayout>
        <div>protected child</div>
      </DashboardLayout>,
    );

    await waitFor(() =>
      expect(screen.getByText(/couldn't reach the server/i)).toBeInTheDocument(),
    );
    // Symmetric with the 500 path: the network branch flows through a
    // different api.ts code path (fetchOrFriendlyThrow → ApiError
    // network.unreachable status 0), so assert it ALSO surfaces a working
    // Retry and gates the protected child — not just "no redirect".
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
    expect(screen.queryByText("protected child")).toBeNull();
    expect(navState.router.replace).not.toHaveBeenCalled();
  });

  it("recovers into the shell when Retry succeeds", async () => {
    let calls = 0;
    server.use(
      http.get(url("/api/auth/me"), () => {
        calls += 1;
        return calls === 1
          ? jsonError("server.error", "Temporary blip.", { status: 500 })
          : jsonOk(authedMe);
      }),
    );

    renderWithProviders(
      <DashboardLayout>
        <div>protected child</div>
      </DashboardLayout>,
    );

    const retry = await waitFor(() =>
      screen.getByRole("button", { name: /retry/i }),
    );
    // Smoke-check the disabled-state wiring (`disabled={isRetrying}`): the
    // button is enabled at rest before a retry is in flight.
    expect(retry).not.toBeDisabled();
    fireEvent.click(retry);

    // Second /me lands a 200 → the shell renders (org context + nav + child).
    await waitFor(() => expect(screen.getByText("protected child")).toBeInTheDocument());
    expect(screen.getAllByText("Acme Inc").length).toBeGreaterThan(0);
    expect(navState.router.replace).not.toHaveBeenCalled();
  });

  it("STILL redirects to /login on a genuine expired/absent session", async () => {
    // /me 401 → api.ts attempts refresh → refresh 401 → useMe maps to null →
    // layout's effect redirects. This is the path that must be preserved.
    server.use(
      http.get(url("/api/auth/me"), () =>
        jsonError("auth.unauthorized", "Session expired.", { status: 401 }),
      ),
      http.post(url("/api/auth/refresh"), () =>
        jsonError("auth.token_expired", "Refresh expired.", { status: 401 }),
      ),
    );

    renderWithProviders(
      <DashboardLayout>
        <div>protected child</div>
      </DashboardLayout>,
    );

    await waitFor(() => expect(navState.router.replace).toHaveBeenCalledWith("/login"));
    // A genuine logout must NOT surface the transient-error card.
    expect(screen.queryByText(/couldn't reach the server/i)).toBeNull();
  });

  it("keeps an ALREADY-LOADED user in the shell when a background /me revalidation fails", async () => {
    // The real-world #182 symptom: a user already working in the app whose
    // periodic /me revalidation blips. TanStack retains the last-good data on
    // a refetch error, so `me.data` stays populated and the guard
    // `me.isError && me.data === undefined` is precisely what keeps them in
    // the shell. A regression simplifying that guard to `if (me.isError)`
    // would yank a working user into the error card — this test pins it.
    const { queryClient } = renderWithProviders(
      <DashboardLayout>
        <div>protected child</div>
      </DashboardLayout>,
      { auth: authedMe },
    );

    // Seeded session → shell renders immediately.
    expect(screen.getByText("protected child")).toBeInTheDocument();

    // Now make the next /me revalidation fail, then force a refetch.
    server.use(
      http.get(url("/api/auth/me"), () =>
        jsonError("server.error", "Background blip.", { status: 500 }),
      ),
    );
    await queryClient.refetchQueries({ queryKey: [...ME_KEY] });

    // The errored revalidation must NOT evict the user: the shell + child
    // stay, no error card, no redirect.
    await waitFor(() => expect(queryClient.getQueryState([...ME_KEY])?.status).toBe("error"));
    expect(screen.getByText("protected child")).toBeInTheDocument();
    expect(screen.queryByText(/couldn't reach the server/i)).toBeNull();
    expect(navState.router.replace).not.toHaveBeenCalled();
  });
});
