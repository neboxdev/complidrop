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

async function request<T>(path: string, init: RequestInitEx = {}): Promise<T> {
  const url = path.startsWith("http") ? path : `${API_BASE}${path}`;
  const headers = new Headers(init.headers ?? {});
  if (init.body && !(init.body instanceof FormData) && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }
  if (init.idempotencyKey) headers.set("Idempotency-Key", init.idempotencyKey);

  let res = await fetch(url, { ...init, credentials: "include", headers });

  if (res.status === 401 && !init.skipRefresh) {
    refreshing = refreshing ?? doRefresh();
    const refreshed = await refreshing;
    refreshing = null;
    if (refreshed) {
      res = await fetch(url, { ...init, credentials: "include", headers });
    }
  }

  if (!res.ok) {
    let code = "server.error";
    let message = res.statusText;
    let correlationId: string | undefined;
    try {
      const body = (await res.json()) as ApiEnvelope<unknown>;
      code = body.error?.code ?? code;
      message = body.error?.message ?? message;
      correlationId = body.error?.correlationId;
    } catch {
      /* non-JSON error */
    }
    throw new ApiError(code, message, res.status, correlationId);
  }

  if (res.status === 204) return undefined as T;

  const body = (await res.json()) as ApiEnvelope<T>;
  if (body.error) throw new ApiError(body.error.code, body.error.message, res.status, body.error.correlationId);
  return body.data as T;
}

export const api = {
  get: <T>(path: string) => request<T>(path, { method: "GET" }),
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
