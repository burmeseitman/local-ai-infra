import { vi } from "vitest";
import type { ModelInfo, ModelListResponse, SystemOverview } from "../types/api";

const encoder = new TextEncoder();

export function model(name: string, overrides: Partial<ModelInfo> = {}): ModelInfo {
  const [displayName, tag] = name.split(":");
  return {
    name,
    displayName: displayName ?? name,
    tag: tag ?? null,
    family: "qwen2",
    digest: "a8b0c5157701",
    sizeBytes: 397_821_319,
    modifiedAt: "2026-01-01T00:00:00Z",
    parameterSize: "494M",
    quantizationLevel: "Q4_K_M",
    isRemote: false,
    isAvailable: true,
    isDefault: false,
    ...overrides,
  };
}

export function modelList(models: ModelInfo[], defaultModel: string | null = "qwen2.5:0.5b"): ModelListResponse {
  return { provider: "ollama", models, defaultModel, fetchedAt: new Date().toISOString() };
}

export function overview(overrides: { reachable?: boolean; usable?: number } = {}): SystemOverview {
  const reachable = overrides.reachable ?? true;
  const usable = overrides.usable ?? 1;
  const now = new Date().toISOString();
  return {
    api: { status: "healthy", version: "1.0.0", environment: "Test", startedAt: now, uptimeSeconds: 42 },
    provider: {
      provider: "ollama",
      reachable,
      version: reachable ? "0.40.4" : null,
      latencyMs: reachable ? 3.2 : null,
      error: reachable ? null : "The local inference service is not reachable.",
      checkedAt: now,
      lastSuccessfulCheckAt: reachable ? now : null,
      installedModelCount: reachable ? usable : null,
      usableModelCount: reachable ? usable : null,
      defaultModel: "qwen2.5:0.5b",
      defaultModelInstalled: reachable && usable > 0,
    },
    inference: {
      since: now,
      totalRequests: 0,
      succeeded: 0,
      failed: 0,
      cancelled: 0,
      activeGenerations: 0,
      lastSuccessAt: null,
      lastFailureAt: null,
      lastCompletionModel: null,
      lastCompletion: null,
      recentFailures: [],
    },
    limits: {
      maxMessageLength: 8000,
      maxConversationLength: 32000,
      maxMessages: 50,
      defaultMaxTokens: 1024,
      maxTokensLimit: 4096,
      maxConcurrentGenerations: 2,
      inferenceTimeoutSeconds: 300,
      allowRemoteModels: false,
    },
    generatedAt: now,
  };
}

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": status >= 400 ? "application/problem+json" : "application/json" },
  });
}

export function sse(event: string, data: unknown): string {
  return `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
}

/** An SSE response whose body the test pushes to manually, to observe incremental rendering. */
export function controlledStream(signal?: AbortSignal | null) {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const body = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
    },
  });
  signal?.addEventListener("abort", () => {
    try {
      controller.error(new DOMException("The operation was aborted.", "AbortError"));
    } catch {
      // already closed
    }
  });
  return {
    response: new Response(body, { status: 200, headers: { "Content-Type": "text/event-stream" } }),
    push: (text: string) => controller.enqueue(encoder.encode(text)),
    close: () => controller.close(),
  };
}

export function streamResponse(...frames: string[]): Response {
  const body = new ReadableStream<Uint8Array>({
    start(c) {
      for (const frame of frames) {
        c.enqueue(encoder.encode(frame));
      }
      c.close();
    },
  });
  return new Response(body, { status: 200, headers: { "Content-Type": "text/event-stream" } });
}

type Handler = (init: RequestInit | undefined) => Response | Promise<Response>;

/** Routes fetch calls by "METHOD path" (query string ignored). Unmatched calls fail the test loudly. */
export function installFetch(routes: Record<string, Handler>) {
  const calls: { key: string; init: RequestInit | undefined }[] = [];
  const mock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(typeof input === "string" ? input : input.toString(), "http://localhost");
    const key = `${init?.method ?? "GET"} ${url.pathname}`;
    calls.push({ key, init });
    const handler = routes[key];
    if (!handler) {
      throw new Error(`Unexpected fetch: ${key}`);
    }
    return handler(init);
  });
  vi.stubGlobal("fetch", mock);
  return { mock, calls, callsTo: (key: string) => calls.filter((c) => c.key === key) };
}
