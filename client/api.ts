/** A refused request: the HTTP status and the server's error code. */
export class ApiError extends Error {
  constructor(readonly status: number, readonly code: string, readonly body: unknown) {
    super(code);
  }
}

/** Calls the server with JSON; a refusal throws an ApiError carrying the server's error code. */
export async function api<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    credentials: "same-origin",
    headers: body === undefined ? {} : { "content-type": "application/json" },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  const data: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const code = data && typeof data === "object" && "error" in data && typeof data.error === "string" ? data.error : "common.error";
    throw new ApiError(response.status, code, data);
  }
  return data as T;
}

export const get = <T>(path: string) => api<T>("GET", path);
export const post = <T>(path: string, body?: unknown) => api<T>("POST", path, body);
export const put = <T>(path: string, body?: unknown) => api<T>("PUT", path, body);
export const del = <T>(path: string) => api<T>("DELETE", path);
