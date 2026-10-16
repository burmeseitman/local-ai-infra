import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it } from "vitest";
import { App } from "../app";
import { documentList, installFetch, json, model, modelList, overview, signedIn } from "../test/fake-api";

function routes(allowCloud: boolean) {
  return {
    "GET /api/auth/state": () => json(signedIn()),
    "GET /api/system/overview": () => json(overview()),
    "GET /api/models": () =>
      json(modelList([
        model("qwen2.5:7b", { isDefault: true }),
        model("glm-5:cloud", { isRemote: true, isAvailable: allowCloud, sizeBytes: null, parameterSize: null }),
      ], "qwen2.5:7b")),
    "GET /api/conversations": () => json({ conversations: [] }),
    "GET /api/documents": () => json(documentList([{ title: "Handbook" }])),
  };
}

describe("Cloud models", () => {
  beforeEach(() => {
    window.location.hash = "#/chat";
    window.localStorage.clear();
  });

  it("are disabled and the app claims local inference when not allowed", async () => {
    installFetch(routes(false));
    render(<App />);

    const select = await screen.findByLabelText("Model");
    await waitFor(() => expect(select).toHaveValue("qwen2.5:7b"));
    expect(within(select).getByRole("option", { name: "glm-5:cloud (cloud — disabled)" })).toBeDisabled();
    expect(screen.getByText("Inference runs locally. No cloud AI service or API key is used.")).toBeInTheDocument();
    expect(screen.queryByText("Cloud model selected")).not.toBeInTheDocument();
  });

  it("are grouped separately, default to local, and warn clearly once selected", async () => {
    installFetch(routes(true));
    render(<App />);
    const user = userEvent.setup();

    const select = await screen.findByLabelText("Model");
    await waitFor(() => expect(select).toHaveValue("qwen2.5:7b")); // the local default wins
    expect(within(select).getByRole("group", { name: "Local (this machine)" })).toBeInTheDocument();
    expect(within(within(select).getByRole("group", { name: "Cloud (runs on ollama.com)" })).getByRole("option")).toHaveTextContent("glm-5:cloud · cloud");

    await user.selectOptions(select, "glm-5:cloud");

    expect(await screen.findByText("Cloud model selected")).toBeInTheDocument();
    expect(screen.getByText(/your message and the conversation history/)).toBeInTheDocument();
    expect(screen.getByText("A cloud model is selected: prompts are sent to ollama.com. Documents are still indexed locally.")).toBeInTheDocument();
    expect(screen.queryByText("Inference runs locally. No cloud AI service or API key is used.")).not.toBeInTheDocument();
    expect(screen.getByText("Ollama cloud")).toBeInTheDocument();

    // With documents on, the warning says passages leave the machine too.
    await user.click(screen.getByRole("checkbox", { name: "Use my documents" }));
    expect(screen.getByText(/plus the matching passages from your documents/)).toBeInTheDocument();

    // Switching back to a local model restores the local-only state.
    await user.selectOptions(select, "qwen2.5:7b");
    expect(screen.queryByText("Cloud model selected")).not.toBeInTheDocument();
    expect(screen.getByText("Inference runs locally. No cloud AI service or API key is used.")).toBeInTheDocument();
  });
});
