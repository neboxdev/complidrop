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
 *   - `renderWithProviders` with `portalInfo` fixture + the
 *     `expiredPortalLinkHandler` factory
 *
 * The subjects under test are intentionally trivial inline components so
 * the template stands on its own without depending on any real page or
 * component — the harness itself is the thing under demonstration.
 */
import { describe, it, expect, vi } from "vitest";
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
  documentsAllStatusesResponse,
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

type PortalInfoLike = {
  vendorName: string;
  orgName: string;
  isActive: boolean;
};

function PortalInner({ token }: { token: string }) {
  // Real dashboards use TanStack Query, so the template does too. Portal
  // tests in #37 will mirror this against the actual portal route, which
  // uses bare fetch — the assertion shape stays the same.
  const probe = useQuery<PortalInfoLike>({
    queryKey: ["portal", token],
    queryFn: () => api.get<PortalInfoLike>(`/api/portal/${token}`),
  });
  if (probe.isPending) return <span>loading portal…</span>;
  if (probe.isError) return <span role="alert">link gone</span>;
  return <span>Hi {probe.data?.vendorName}</span>;
}

function PortalGreeting() {
  const params = useParams<{ token: string }>();
  const router = useRouter();
  return (
    <div>
      <span data-testid="token">token: {params.token}</span>
      <button onClick={() => router.push(`/portal/${params.token}/done`)}>
        finish
      </button>
      <PortalInner token={params.token} />
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
    server.use(
      http.get(url("/api/documents"), () => jsonOk(documentsAllStatusesResponse)),
    );

    renderWithProviders(<DocsBadge />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByText(/Hi Acme Owner — 4 documents/),
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
    server.use(http.get(url("/api/portal/abc"), () => jsonOk(portalInfo)));

    const pushSpy = vi.fn();
    const { queryClient } = renderWithProviders(<PortalGreeting />, {
      auth: authedMe,
      params: { token: "abc" },
      router: { push: pushSpy },
    });

    expect(screen.getByTestId("token")).toHaveTextContent("token: abc");
    await waitFor(() =>
      expect(
        screen.getByText(`Hi ${portalInfo.vendorName}`),
      ).toBeInTheDocument(),
    );

    fireEvent.click(screen.getByRole("button", { name: /finish/i }));
    expect(pushSpy).toHaveBeenCalledWith("/portal/abc/done");

    // queryClient access lets tests assert post-mutation cache state
    // without re-querying the network. Here we just confirm the harness
    // returns a real client whose cache reflects the fetched portal info.
    expect(queryClient.getQueryData(["portal", "abc"])).toMatchObject({
      vendorName: portalInfo.vendorName,
    });
  });

  it("portal: expiredPortalLinkHandler() drops the link-gone branch", async () => {
    server.use(expiredPortalLinkHandler("abc"));

    renderWithProviders(<PortalGreeting />, {
      auth: authedMe,
      params: { token: "abc" },
    });

    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent(/link gone/i),
    );
  });
});
