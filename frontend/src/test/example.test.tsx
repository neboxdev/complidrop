/**
 * Template test for the frontend harness — **copy this file as the
 * starting point for new component/hook suites.** It exercises every
 * primitive in `src/test/` against a tiny inline subject so reviewers
 * can read end-to-end how the parts fit together:
 *
 *   - `renderWithProviders` (auth-cache seeding + QueryClient + nav state)
 *   - MSW (`server.use` + `http.get` + `url(...)` + `jsonOk` / `jsonError`)
 *   - Named fixtures (`authedMe`, `documentsAllStatusesResponse`)
 *
 * The Subject is intentionally trivial: a `<DocsBadge />` that reads
 * `useMe()`, and only mounts `<AuthedBadge />` (which reads
 * `useDocuments()`) when authed. The two-component split is deliberate —
 * it keeps the anonymous test path genuinely network-free, which is the
 * AC the harness is built to make easy.
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  documentsAllStatusesResponse,
} from "@/test";
import { useMe } from "@/hooks/useAuth";
import { useDocuments } from "@/hooks/useDocuments";

// ---- Subject under test (inline so the template doesn't depend on any
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

describe("Template: renderWithProviders + MSW + fixtures", () => {
  it("anonymous (auth: null) renders the logged-out branch without a network call", () => {
    // `auth: null` seeds the useMe cache with null, so the subject's first
    // render hits the "Sign in" branch synchronously — no MSW handler
    // needed for /api/auth/me, no waitFor, no fetch ever fires.
    renderWithProviders(<DocsBadge />, { auth: null });

    expect(screen.getByText(/sign in to see your documents/i)).toBeInTheDocument();
  });

  it("authed (auth: authedMe) + documents fixture renders the populated badge", async () => {
    // The documents page hits GET /api/documents; declare that handler for
    // THIS test, using the shared `documentsAllStatusesResponse` fixture.
    server.use(
      http.get(url("/api/documents"), () => jsonOk(documentsAllStatusesResponse)),
    );

    renderWithProviders(<DocsBadge />, { auth: authedMe });

    // `useDocuments` is async, so wait for the final greeting (the in-flight
    // "counting…" appears first). Using waitFor here, not findByText, so the
    // expectation reads as a single transition rather than a polling loop.
    await waitFor(() =>
      expect(
        screen.getByText(/Hi Acme Owner — 4 documents/),
      ).toBeInTheDocument(),
    );
  });

  it("authed + GET /api/documents 5xx surfaces the error-copy branch", async () => {
    // Demonstrates the error-envelope path: handlers return the same
    // `{ data, error }` shape lib/api.ts parses, so the api client throws
    // an `ApiError` and the hook flips to `isError`.
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
});
