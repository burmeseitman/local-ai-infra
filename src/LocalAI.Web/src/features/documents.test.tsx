import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it } from "vitest";
import { App } from "../app";
import { documentList, installFetch, json, model, modelList, overview, signedIn, sse, streamResponse, conversation } from "../test/fake-api";

const SEND = "POST /api/conversations/:id/messages/stream";

function routes(documents = documentList()) {
  return {
    "GET /api/auth/state": () => json(signedIn()),
    "GET /api/system/overview": () => json(overview()),
    "GET /api/models": () => json(modelList([model("qwen2.5:3b", { isDefault: true }), model("nomic-embed-text:latest", { isEmbedding: true, isAvailable: false })], "qwen2.5:3b")),
    "GET /api/conversations": () => json({ conversations: [] }),
    "GET /api/documents": () => json(documents),
    "POST /api/conversations": () => json(conversation("c1", "New conversation"), 201),
  };
}

const header = (init: RequestInit | undefined, name: string) => new Headers(init?.headers).get(name);

describe("Documents", () => {
  beforeEach(() => {
    window.location.hash = "#/documents";
  });

  it("explains how to install the embedding model and disables upload until then", async () => {
    installFetch(routes(documentList([], false)));
    render(<App />);

    expect(await screen.findByText("Embedding model not installed")).toBeInTheDocument();
    expect(screen.getByText("docker compose exec ollama ollama pull nomic-embed-text")).toBeInTheDocument();
    expect(screen.getByLabelText("Upload documents")).toBeDisabled();
  });

  it("uploads a file as multipart with the CSRF header and lists indexing status", async () => {
    let docs = documentList();
    const api = installFetch({
      ...routes(),
      "GET /api/documents": () => json(docs),
      "POST /api/documents": () => {
        docs = documentList([{ title: "Handbook", fileName: "Handbook.pdf", status: "processing", chunkCount: 0 }]);
        return json(docs.documents[0], 202);
      },
    });
    render(<App />);
    const user = userEvent.setup();

    const input = await screen.findByLabelText("Upload documents");
    await waitFor(() => expect(input).toBeEnabled());
    await user.upload(input, new File(["%PDF-1.7 ..."], "Handbook.pdf", { type: "application/pdf" }));

    const call = await waitFor(() => api.callsTo("POST /api/documents")[0]!);
    expect(call.init!.body).toBeInstanceOf(FormData);
    expect((call.init!.body as FormData).get("file")).toBeInstanceOf(File);
    expect(header(call.init, "X-LocalAI-Request")).toBe("1");
    expect(await screen.findByText("Indexing…")).toBeInTheDocument();
    expect(screen.getByText("Handbook.pdf")).toBeInTheDocument();
  });

  it("shows upload errors from the server", async () => {
    installFetch({
      ...routes(),
      "POST /api/documents": () => json({ status: 415, code: "unsupported_document_type", detail: "Unsupported file type '.docx'." }, 415),
    });
    render(<App />);
    const user = userEvent.setup({ applyAccept: false });

    const input = await screen.findByLabelText("Upload documents");
    await waitFor(() => expect(input).toBeEnabled());
    await user.upload(input, new File(["x"], "notes.docx"));

    expect(await screen.findByText(/Unsupported file type '.docx'/)).toBeInTheDocument();
  });

  it("shows failed documents with their reason and deletes after confirmation", async () => {
    let docs = documentList([{ title: "Scan", status: "failed", error: "No text could be extracted." }]);
    const api = installFetch({
      ...routes(),
      "GET /api/documents": () => json(docs),
      "DELETE /api/documents/:id": () => {
        docs = documentList();
        return new Response(null, { status: 204 });
      },
    });
    render(<App />);
    const user = userEvent.setup();

    expect(await screen.findByText("No text could be extracted.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Delete Scan" }));
    await user.click(within(screen.getByRole("table")).getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(api.callsTo("DELETE /api/documents/:id")).toHaveLength(1));
    expect(await screen.findByText("No documents yet.")).toBeInTheDocument();
  });
});

describe("Chat with documents", () => {
  beforeEach(() => {
    window.location.hash = "#/chat";
    window.localStorage.clear();
  });

  it("disables the toggle until a document is ready", async () => {
    installFetch(routes(documentList([{ status: "processing" }])));
    render(<App />);

    const toggle = await screen.findByRole("checkbox", { name: "Use my documents" });
    expect(toggle).toBeDisabled();
    expect(screen.getByRole("link", { name: "Add documents" })).toHaveAttribute("href", "#/documents");
  });

  it("sends useDocuments and renders cited sources from the stream", async () => {
    const api = installFetch({
      ...routes(documentList([{ title: "Handbook" }])),
      [SEND]: () =>
        streamResponse(
          sse("start", { requestId: "r", model: "qwen2.5:3b", createdAt: "", conversationId: "c1" }),
          sse("sources", { sources: [{ number: 1, documentId: "doc-0", title: "Handbook", page: 3, snippet: "Employees receive 25 vacation days.", score: 0.81 }] }),
          sse("delta", { content: "You get 25 days [1]." }),
          sse("done", { finishReason: "stop", stats: null }),
        ),
    });
    render(<App />);
    const user = userEvent.setup();

    const toggle = await screen.findByRole("checkbox", { name: "Use my documents" });
    await waitFor(() => expect(toggle).toBeEnabled());
    await user.click(toggle);
    expect(screen.getByText("Answers cite your 1 document.")).toBeInTheDocument();

    const box = screen.getByLabelText("Message");
    await waitFor(() => expect(box).toBeEnabled());
    await user.type(box, "How many vacation days?{Enter}");

    expect(await screen.findByText("You get 25 days [1].")).toBeInTheDocument();
    expect(screen.getByText("1 source from your documents")).toBeInTheDocument();
    expect(screen.getByText("Handbook")).toBeInTheDocument();
    expect(screen.getByText("· p. 3")).toBeInTheDocument();
    expect(screen.getByText("Employees receive 25 vacation days.")).toBeInTheDocument();
    const body = JSON.parse(api.callsTo(SEND)[0]!.init!.body as string);
    expect(body).toEqual({ model: "qwen2.5:3b", content: "How many vacation days?", useDocuments: true });
    expect(header(api.callsTo(SEND)[0]!.init, "X-LocalAI-Request")).toBe("1");
    expect(header(api.callsTo("POST /api/conversations")[0]!.init, "X-LocalAI-Request")).toBe("1");
  });

  it("says so when no passage matched", async () => {
    window.localStorage.setItem("localai.useDocuments", "true");
    installFetch({
      ...routes(documentList([{ title: "Handbook" }])),
      [SEND]: () => streamResponse(sse("sources", { sources: [] }), sse("delta", { content: "Not covered." }), sse("done", { finishReason: "stop", stats: null })),
    });
    render(<App />);
    const user = userEvent.setup();

    const box = await screen.findByLabelText("Message");
    await waitFor(() => expect(box).toBeEnabled());
    await act(async () => user.type(box, "Unrelated question{Enter}"));

    expect(await screen.findByText("No matching passages were found in your documents.")).toBeInTheDocument();
  });

  it("never offers the embedding model as a chat model", async () => {
    installFetch(routes());
    render(<App />);

    const select = await screen.findByLabelText("Model");
    await waitFor(() => expect(select).toHaveValue("qwen2.5:3b"));
    expect(within(select).queryByText(/nomic-embed-text/)).not.toBeInTheDocument();
  });
});
