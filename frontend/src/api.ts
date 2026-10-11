import type { Auth } from "./types";
export const API_BASE =
  (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(
    /\/$/,
    "",
  ) ?? "";
let auth: Auth | null = null;
let generation = 0;
export function setSession(value: Auth | null) {
  auth = value;
  generation++;
  window.dispatchEvent(new Event("session-changed"));
}
export function getSession() {
  return auth;
}
export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public correlationId?: string,
  ) {
    super(message);
  }
}
export async function request<T>(
  path: string,
  options: { method?: string; body?: unknown; signal?: AbortSignal } = {},
): Promise<T> {
  const startGeneration = generation;
  const signal = options.signal
    ? AbortSignal.any([options.signal, AbortSignal.timeout(60000)])
    : AbortSignal.timeout(60000);
  const headers: Record<string, string> = { Accept: "application/json" };
  if (auth) headers.Authorization = `Bearer ${auth.token}`;
  let body: BodyInit | undefined;
  if (options.body instanceof FormData) body = options.body;
  else if (options.body !== undefined) {
    body = JSON.stringify(options.body);
    headers["Content-Type"] = "application/json";
  }
  let res: Response;
  try {
    res = await fetch(API_BASE + path, {
      method: options.method ?? "GET",
      headers,
      body,
      signal,
    });
  } catch (e) {
    if (options.signal?.aborted) throw e;
    throw new Error(
      "The API could not be reached or the request timed out. Refresh before retrying an action.",
    );
  }
  const payload = await res.json().catch(() => null);
  if (!res.ok || payload?.success === false) {
    if (res.status === 401 && startGeneration === generation && auth)
      setSession(null);
    const message =
      payload?.error?.message ?? `Request failed (${res.status}).`;
    throw new ApiError(message, res.status, payload?.correlationId);
  }
  if (!payload || payload.success !== true)
    throw new Error("Unexpected API response. Check the server URL.");
  if (generation !== startGeneration && !path.startsWith("/api/auth/"))
    throw new Error(
      "The account changed while the request was running. Refresh this view.",
    );
  return payload.data as T;
}
export async function imageBlob(
  path: string,
  signal: AbortSignal,
): Promise<Blob> {
  if (
    !/^\/(uploads\/[a-f0-9]{32}\.(jpg|png|webp)|demo\/[a-z]+\.svg)$/.test(path)
  )
    throw new Error("Invalid evidence URL");
  const res = await fetch(API_BASE + path, {
    headers: auth ? { Authorization: `Bearer ${auth.token}` } : {},
    signal,
  });
  if (!res.ok) throw new Error("Evidence unavailable");
  return res.blob();
}
export async function login(email: string, password: string) {
  const result = await request<Auth>("/api/auth/login", {
    method: "POST",
    body: { email, password },
  });
  setSession(result);
  return result;
}
