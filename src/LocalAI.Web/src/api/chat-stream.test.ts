import { describe, expect, it, vi } from "vitest";
import { installFetch, json, sse, streamResponse } from "../test/fake-api";
import { streamChat } from "./chat-stream";
import { ApiError } from "./http";

const request = { model: "qwen2.5:0.5b", messages: [{ role: "user" as const, content: "Hi" }] };

describe("streamChat", () => {
  it("delivers deltas in order and resolves with the done event", async () => {
    installFetch({
      "POST /api/chat/stream": () =>
        streamResponse(
          sse("start", { requestId: "r1", model: "qwen2.5:0.5b", createdAt: "now" }),
          sse("delta", { content: "Hel" }) + sse("delta", { content: "lo" }).slice(0, 10),
          sse("delta", { content: "lo" }).slice(10),
          sse("done", { finishReason: "stop", stats: null }),
        ),
    });
    const onDelta = vi.fn();
    const onStart = vi.fn();

    const done = await streamChat(request, { onDelta, onStart }, new AbortController().signal);

    expect(onStart).toHaveBeenCalledWith(expect.objectContaining({ requestId: "r1" }));
    expect(onDelta.mock.calls.map((c) => c[0])).toEqual(["Hel", "lo"]);
    expect(done.finishReason).toBe("stop");
  });

  it("rejects with the server's structured error event", async () => {
    installFetch({
      "POST /api/chat/stream": () =>
        streamResponse(
          sse("delta", { content: "partial" }),
          sse("error", { code: "provider_timeout", message: "Timed out.", retryable: true }),
        ),
    });

    const error = await streamChat(request, { onDelta: () => {} }, new AbortController().signal).catch((e) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ code: "provider_timeout", message: "Timed out.", retryable: true });
  });

  it("maps Problem Details responses to ApiError", async () => {
    installFetch({
      "POST /api/chat/stream": () =>
        json({ status: 404, title: "Model not found", detail: "Model 'x' is not installed.", code: "model_not_found", retryable: false }, 404),
    });

    const error = await streamChat(request, { onDelta: () => {} }, new AbortController().signal).catch((e) => e);

    expect(error).toMatchObject({ code: "model_not_found", status: 404, message: "Model 'x' is not installed." });
  });

  it("reports a stream that ends without a terminal event", async () => {
    installFetch({ "POST /api/chat/stream": () => streamResponse(sse("delta", { content: "a" })) });

    const error = await streamChat(request, { onDelta: () => {} }, new AbortController().signal).catch((e) => e);

    expect(error).toMatchObject({ code: "stream_incomplete", retryable: true });
  });

  it("reports network failures without leaking internals", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("Failed to fetch")));

    const error = await streamChat(request, { onDelta: () => {} }, new AbortController().signal).catch((e) => e);

    expect(error).toMatchObject({ code: "network_error" });
  });
});
