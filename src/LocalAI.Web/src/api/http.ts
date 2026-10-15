import type { ProblemDetails } from "../types/api";

/** Empty string means "same origin" (Vite proxy in development, nginx in Docker). */
export const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/+$/, "");

/** Same-origin by default; cross-origin API deployments need cookies sent explicitly. */
const CREDENTIALS: RequestCredentials = API_BASE_URL ? "include" : "same-origin";

let unauthorizedHandler: (() => void) | null = null;

/** Called whenever the API reports that the session is missing or expired. */
export function onUnauthorized(handler: (() => void) | null): void {
  unauthorizedHandler = handler;
}

export class ApiError extends Error {
  readonly code: string;
  readonly status: number | null;
  readonly retryable: boolean;

  constructor(message: string, code: string, status: number | null, retryable: boolean) {
    super(message);
    this.name = "ApiError";
    this.code = code;
    this.status = status;
    this.retryable = retryable;
  }
}

export { CREDENTIALS };

export function isAbortError(error: unknown): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "name" in error &&
    (error as { name: unknown }).name === "AbortError"
  );
}

export function networkError(): ApiError {
  return new ApiError(
    "Cannot reach the API. Check that the backend is running.",
    "network_error",
    null,
    true,
  );
}

/** Converts a non-2xx response (ideally Problem Details) into an ApiError. */
export async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails | null = null;
  try {
    problem = (await response.json()) as ProblemDetails;
  } catch {
    // Not JSON (e.g. a proxy error page); fall back to the status code.
  }

  const validation = problem?.errors
    ? Object.values(problem.errors).flat().join(" ")
    : null;
  const message =
    validation ||
    problem?.detail ||
    problem?.title ||
    `Request failed with HTTP ${response.status}.`;
  if (problem?.code === "unauthenticated") {
    unauthorizedHandler?.();
  }
  return new ApiError(
    message,
    problem?.code ?? `http_${response.status}`,
    response.status,
    problem?.retryable ?? response.status >= 500,
  );
}

export async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${API_BASE_URL}${path}`, {
      headers: { Accept: "application/json" },
      credentials: CREDENTIALS,
      signal,
    });
  } catch (error) {
    if (isAbortError(error)) {
      throw error;
    }
    throw networkError();
  }

  if (!response.ok) {
    throw await toApiError(response);
  }
  return (await response.json()) as T;
}

/** JSON request with a body (POST/PATCH/DELETE). Returns null for 204 No Content. */
export async function sendJson<T>(method: string, path: string, body?: unknown): Promise<T | null> {
  let response: Response;
  try {
    response = await fetch(`${API_BASE_URL}${path}`, {
      method,
      headers: body === undefined ? { Accept: "application/json" } : { "Content-Type": "application/json", Accept: "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: CREDENTIALS,
    });
  } catch {
    throw networkError();
  }

  if (!response.ok) {
    throw await toApiError(response);
  }
  return response.status === 204 ? null : ((await response.json()) as T);
}
