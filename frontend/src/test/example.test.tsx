/**
 * Template test for the frontend harness — **copy this file as the
 * starting point for new component/hook suites.** Exercises every
 * primitive in `src/test/` so a regression in any one of them fails this
 * file:
 *
 *   - `renderWithProviders` with `auth: null` (seeded anonymous, no fetch)
 *   - `renderWithProviders` with `auth: authedMe` + MSW handler + fixture
 *   - `renderWithProviders` with `auth + GET 5xx` error-envelope path
 *   - `renderWithProviders` with `params` / `router` / returned `queryClient`
 *   - `renderWithProviders` driven by the `expiredPortalLinkHandler` factory
 *
 * The subjects under test are intentionally trivial inline components so
 * the template stands on its own without depending on any real page or
 * component — the harness itself is the thing under demonstration.
 *
 * NOTE for the upcoming portal-page suite (#37): the inline `SettingsTile`
 * below uses `useQuery` + `api.get<…>(…)` because that's the cookie-bearing
 * dashboard pattern. The real `frontend/src/app/portal/[token]/page.tsx`
 * uses bare `fetch()` and parses the envelope inline — portal tests must
 * follow THAT pattern (assert on inline error-string state, not on
 * `useQuery.isError`). See `src/test/README.md` ("Portal-page caveat").
 */
import { describe, it, expect, vi } from "vitest";
import { useEffect, useState } from "react";
import { http } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import { useQuery } from "@tanstack/react-query";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  documentsAllStatuses,
  makeDocumentsResponse,
  portalInfo,
  expiredPortalLinkHandler,
} from "@/test";
import { useMe } from "@/hooks/useAuth";
import { useDocuments } from "@/hooks/useDocuments";
import { useParams, useRouter } from "next/navigation";
import { api } from "@/lib/api";

// ---- Subjects under test (inline so the template doesn't depend on any
// real page; in a real suite you'd import a component from the app tree).

function AuthedBadge({ name }: { name: string }) {
  const docs = useDocuments();
  if (docs.isPending) return <span>Hi {name} — counting…</span>;
  if (docs.isError) return <span role="alert">Couldn’t load documents</span>;
  return (
    <span>
      Hi {name} — {docs.data?.items.length ?? 0} documents
    </span>
  );
}

function DocsBadge() {
  const me = useMe();
  if (me.data === undefined) return <span>loading…</span>;
  if (me.data === null) return <span>Sign in to see your documents</span>;
  // `useDocuments` is only mounted on the authed branch — the anonymous
  // render path makes ZERO network calls, which is the contract the
  // harness's `onUnhandledRequest: "error"` enforces.
  return <AuthedBadge name={me.data.fullName} />;
}

type SettingsResponse = {
  organizationName: string;
};

function SettingsTile() {
  // A dashboard-style settings tile: reads `:tab` route param, fetches
  // tab-scoped settings via the api client, and exposes a "save" button
  // that re-navigates. Covers `params` + `router.push` spy + the api
  // client's full envelope-error path.
  const params = useParams<{ tab: string }>();
  const router = useRouter();
  const settings = useQuery<SettingsResponse>({
    queryKey: ["settings", params.tab],
    queryFn: () => api.get<SettingsResponse>(`/api/settings/${params.tab}`),
  });
  // The tab label is param-driven and available synchronously — render it
  // outside the loading gate so tests can assert on it without waiting.
  return (
    <div>
      <span data-testid="tab">tab: {params.tab}</span>
      {settings.isPending && <span>loading settings…</span>}
      {settings.isError && <span role="alert">couldn’t load settings</span>}
      {settings.data && (
        <>
          <span>org: {settings.data.organizationName}</span>
          <button onClick={() => router.push("/settings/saved")}>save</button>
        </>
      )}
    </div>
  );
}

describe("Template: renderWithProviders + MSW + fixtures", () => {
  it("anonymous (auth: null) renders the logged-out branch without a network call", () => {
    // Pin the no-fetch contract: if seeding regresses (e.g. ME_KEY drifts),
    // useMe() would fall through to MSW's default /api/auth/me handler and
    // the assertion below would still pass (401 maps to null → "Sign in" UI).
    // Override the default handler to THROW so a stray fetch fails the
    // test loudly with a useful message instead of silently passing.
    server.use(
      http.get(url("/api/auth/me"), () => {
        throw new Error(
          "seed regression — auth: null should not fetch /api/auth/me",
        );
      }),
    );

    renderWithProviders(<DocsBadge />, { auth: null });

    expect(screen.getByText(/sign in to see your documents/i)).toBeInTheDocument();
  });

  it("authed (auth: authedMe) + documents fixture renders the populated badge", async () => {
    // Use a completed-only response so the happy-path test doesn't schedule
    // `useDocuments`'s 5-second `refetchInterval` (which fires only when
    // Pending/Processing rows are present in the fixture). Cleanup would
    // cancel the timer regardless, but the explicit fixture variant keeps
    // the test predictable on slow CI machines.
    const completedOnly = makeDocumentsResponse({
      items: [{ ...documentsAllStatuses[2] }],
      total: 1,
    });
    server.use(http.get(url("/api/documents"), () => jsonOk(completedOnly)));

    renderWithProviders(<DocsBadge />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByText(/Hi Acme Owner — 1 documents/),
      ).toBeInTheDocument(),
    );
  });

  it("authed + GET /api/documents 5xx surfaces the error-copy branch", async () => {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonError("server.error", "DB went on vacation", { status: 500 }),
      ),
    );

    renderWithProviders(<DocsBadge />, { auth: authedMe });

    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent(
        /couldn’t load documents/i,
      ),
    );
  });

  it("exercises params + router.push spy + returned queryClient introspection", async () => {
    server.use(
      http.get(url("/api/settings/billing"), () =>
        jsonOk({ organizationName: "Acme Inc" } satisfies SettingsResponse),
      ),
    );

    const pushSpy = vi.fn();
    const { queryClient } = renderWithProviders(<SettingsTile />, {
      auth: authedMe,
      params: { tab: "billing" },
      router: { push: pushSpy },
    });

    expect(screen.getByTestId("tab")).toHaveTextContent("tab: billing");
    await waitFor(() =>
      expect(screen.getByText("org: Acme Inc")).toBeInTheDocument(),
    );

    fireEvent.click(screen.getByRole("button", { name: /save/i }));
    expect(pushSpy).toHaveBeenCalledWith("/settings/saved");

    // queryClient access lets tests assert post-mutation cache state
    // without re-querying the network. Here we just confirm the harness
    // returns a real client whose cache reflects the fetched settings.
    expect(queryClient.getQueryData(["settings", "billing"])).toMatchObject({
      organizationName: "Acme Inc",
    });
  });

  it("portal: expiredPortalLinkHandler + portalInfo demonstrate the portal-style fixtures", async () => {
    // Inline portal-shape subject: bare fetch (NOT the api client), inline
    // envelope parse — this is what the real `portal/[token]/page.tsx`
    // does. Portal tests in #37 will mirror this shape; tickets #35/#36
    // use the SettingsTile shape above.
    function MiniPortal() {
      const { token } = useParams<{ token: string }>();
      const [state, setState] = useState<{ info: typeof portalInfo | null; err: string | null }>({
        info: null,
        err: null,
      });
      useEffect(() => {
        let alive = true;
        fetch(url(`/api/portal/${token}`))
          .then(async (r) => {
            const body = (await r.json()) as {
              data: typeof portalInfo | null;
              error: { message: string } | null;
            };
            if (!alive) return;
            if (body.error) setState({ info: null, err: body.error.message });
            else setState({ info: body.data, err: null });
          })
          .catch(() => alive && setState({ info: null, err: "network" }));
        return () => {
          alive = false;
        };
      }, [token]);
      if (state.err) return <span role="alert">{state.err}</span>;
      if (!state.info) return <span>loading portal…</span>;
      return <span>Hi {state.info.vendorName}</span>;
    }

    // The handler matches /api/portal/abc literally; pair the matching
    // token in the route params.
    server.use(expiredPortalLinkHandler("abc"));

    renderWithProviders(<MiniPortal />, {
      auth: authedMe,
      params: { token: "abc" },
    });

    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent(
        /this link is no longer available/i,
      ),
    );
  });
});
