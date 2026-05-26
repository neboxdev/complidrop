export type ApiEnvelope<T> = {
  data: T | null;
  error: { code: string; message: string; correlationId?: string } | null;
};

const API_BASE = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5292";

let refreshing: Promise<boolean> | null = null;

async function doRefresh(): Promise<boolean> {
  try {
    const res = await fetch(`${API_BASE}/api/auth/refresh`, {
      method: "POST",
      credentials: "include",
    });
    return res.ok;
  } catch {
    return false;
  }
}

export class ApiError extends Error {
  constructor(
    public code: string,
    message: string,
    public status: number,
    public correlationId?: string,
  ) {
    super(message);
  }
}

type RequestInitEx = RequestInit & { skipRefresh?: boolean; idempotencyKey?: string };

// User-facing fallback when the server's error message is unavailable
// (non-JSON 5xx body) OR when fetch() itself rejects (network failure,
// CORS drop, offline). #35's AC #3 demands jargon-free toast copy — no
// raw HTTP statusText ("Bad Gateway"), no browser TypeError ("Failed to
// fetch") — so api.ts converts both to this string before the page
// layer forwards it to toast.error / a list-error-card. See #77 for the
// decision and the rejected option (b) of exempting opaque proxy errors.
const GENERIC_FALLBACK_MESSAGE = "Something went wrong. Try again.";

async function fetchOrFriendlyThrow(
  url: string,
  init: RequestInit,
): Promise<Response> {
  // fetch() throws a TypeError on network failure (offline, DNS, CORS,
  // connection reset). Wrap it in an ApiError so callers get the same
  // shape they get from a non-OK HTTP response — `err.message` stays
  // jargon-free and `err.status === 0` signals "never reached the
  // server" without leaking the browser's raw TypeError string.
  //
  // AbortError (DOMException) is deliberately re-thrown unchanged. A
  // future caller that passes an AbortSignal (e.g. via TanStack
  // Query's `queryFn({ signal })`) needs the cancellation to surface
  // as a cancellation, NOT as a network error — otherwise an unmount
  // / route-change mid-fetch would trigger a real-looking toast.
  //
  // doRefresh() at the top of this module keeps its own bare-fetch
  // try/catch returning false; only request()'s body calls funnel
  // through here.
  try {
    return await fetch(url, init);
  } catch (err) {
    if (err instanceof DOMException && err.name === "AbortError") throw err;
    throw new ApiError("network.unreachable", GENERIC_FALLBACK_MESSAGE, 0);
  }
}

async function request<T>(path: string, init: RequestInitEx = {}): Promise<T> {
  const url = path.startsWith("http") ? path : `${API_BASE}${path}`;
  const headers = new Headers(init.headers ?? {});
  if (init.body && !(init.body instanceof FormData) && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }
  if (init.idempotencyKey) headers.set("Idempotency-Key", init.idempotencyKey);

  let res = await fetchOrFriendlyThrow(url, { ...init, credentials: "include", headers });

  if (res.status === 401 && !init.skipRefresh) {
    refreshing = refreshing ?? doRefresh();
    const refreshed = await refreshing;
    refreshing = null;
    if (refreshed) {
      res = await fetchOrFriendlyThrow(url, { ...init, credentials: "include", headers });
    }
  }

  if (!res.ok) {
    let code = "server.error";
    // Initialize to the generic fallback BEFORE attempting the envelope
    // parse. If res.json() fails (non-JSON body — most commonly a
    // Cloudflare/proxy/CDN HTML error page on a 502/503), the fallback
    // wins; only a successful envelope parse with a present
    // body.error.message can override it. statusText ("Bad Gateway",
    // "Service Unavailable") is HTTP jargon hostile to the SMB target
    // audience (#77).
    let message = GENERIC_FALLBACK_MESSAGE;
    let correlationId: string | undefined;
    try {
      const body = (await res.json()) as ApiEnvelope<unknown>;
      code = body.error?.code ?? code;
      // .trim() guards against a server that returns
      // `{ error: { message: "" } }` (or whitespace-only). `??`
      // alone would let an empty string overwrite the fallback and
      // surface an empty toast. The trimmed string is preserved as
      // the override only when it has actual content.
      const envMessage = body.error?.message?.trim();
      if (envMessage) message = envMessage;
      correlationId = body.error?.correlationId;
    } catch {
      /* non-JSON error body — message stays as the generic fallback */
    }
    throw new ApiError(code, message, res.status, correlationId);
  }

  if (res.status === 204) return undefined as T;

  const body = (await res.json()) as ApiEnvelope<T>;
  if (body.error) throw new ApiError(body.error.code, body.error.message, res.status, body.error.correlationId);
  return body.data as T;
}

export const api = {
  get: <T>(path: string, opts: Omit<RequestInitEx, "method" | "body"> = {}) =>
    request<T>(path, { method: "GET", ...opts }),
  post: <T>(path: string, body?: unknown, opts: Omit<RequestInitEx, "method" | "body"> = {}) =>
    request<T>(path, {
      method: "POST",
      body: body === undefined ? undefined : JSON.stringify(body),
      ...opts,
    }),
  put: <T>(path: string, body?: unknown) =>
    request<T>(path, {
      method: "PUT",
      body: body === undefined ? undefined : JSON.stringify(body),
    }),
  delete: <T>(path: string) => request<T>(path, { method: "DELETE" }),
  postForm: <T>(path: string, form: FormData, opts: Omit<RequestInitEx, "method" | "body"> = {}) =>
    request<T>(path, { method: "POST", body: form, ...opts }),
};
