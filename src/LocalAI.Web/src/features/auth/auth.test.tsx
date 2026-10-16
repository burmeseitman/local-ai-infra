import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it } from "vitest";
import { listConversations } from "../../api/conversations";
import { App } from "../../app";
import { authUser, documentList, installFetch, json, modelList, overview, signedIn } from "../../test/fake-api";

function dashboardRoutes() {
  return {
    "GET /api/system/overview": () => json(overview()),
    "GET /api/models": () => json(modelList([])),
    "GET /api/conversations": () => json({ conversations: [] }),
    "GET /api/documents": () => json(documentList()),
  };
}

const problem = (status: number, code: string, detail: string) => json({ status, code, detail, retryable: false }, status);

describe("Authentication", () => {
  beforeEach(() => {
    window.location.hash = "#/chat";
  });

  it("walks through first-run setup and lands on the dashboard", async () => {
    const api = installFetch({
      ...dashboardRoutes(),
      "GET /api/auth/state": () => json({ setupRequired: true, user: null }),
      "POST /api/auth/setup": () => json(authUser("owner")),
    });
    render(<App />);
    const user = userEvent.setup();

    expect(await screen.findByRole("heading", { name: "Create the administrator account" })).toBeInTheDocument();
    await user.type(screen.getByLabelText("Username"), "owner");
    await user.type(screen.getByLabelText("Password"), "a-long-password");
    await user.type(screen.getByLabelText("Confirm password"), "different-password");
    await user.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The passwords do not match.");
    expect(api.callsTo("POST /api/auth/setup")).toHaveLength(0);

    await user.clear(screen.getByLabelText("Confirm password"));
    await user.type(screen.getByLabelText("Confirm password"), "a-long-password");
    await user.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByRole("heading", { name: "New conversation" })).toBeInTheDocument();
    expect(JSON.parse(api.callsTo("POST /api/auth/setup")[0]!.init!.body as string)).toEqual({ username: "owner", password: "a-long-password" });
    // The dashboard only starts loading data once signed in.
    expect(api.calls.findIndex((c) => c.key === "GET /api/conversations")).toBeGreaterThan(
      api.calls.findIndex((c) => c.key === "POST /api/auth/setup"),
    );
  });

  it("shows the server's error for bad credentials and signs in on success", async () => {
    let attempts = 0;
    installFetch({
      ...dashboardRoutes(),
      "GET /api/auth/state": () => json({ setupRequired: false, user: null }),
      "POST /api/auth/login": () =>
        ++attempts === 1 ? problem(401, "invalid_credentials", "The username or password is incorrect.") : json(authUser("bob", "user")),
    });
    render(<App />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText("Username"), "bob");
    await user.type(screen.getByLabelText("Password"), "wrong");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The username or password is incorrect.");

    await user.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByText("bob")).toBeInTheDocument();
  });

  it("returns to sign-in with a notice when the session expires mid-use", async () => {
    let expired = false;
    installFetch({
      ...dashboardRoutes(),
      "GET /api/auth/state": () => json(signedIn()),
      "GET /api/conversations": () => (expired ? problem(401, "unauthenticated", "Sign in to use this endpoint.") : json({ conversations: [] })),
    });
    render(<App />);
    expect(await screen.findByRole("heading", { name: "New conversation" })).toBeInTheDocument();

    // Any API call that answers "unauthenticated" signs the whole UI out.
    expired = true;
    await listConversations().catch(() => undefined);

    expect(await screen.findByText("Your session has ended. Please sign in again.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sign in" })).toBeInTheDocument();
  });

  it("shows the Users page only to administrators", async () => {
    installFetch({ ...dashboardRoutes(), "GET /api/auth/state": () => json(signedIn("bob", "user")) });
    const { unmount } = render(<App />);
    expect(await screen.findByText("bob")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Users" })).not.toBeInTheDocument();
    unmount();

    installFetch({ ...dashboardRoutes(), "GET /api/auth/state": () => json(signedIn("admin", "admin")) });
    render(<App />);
    expect(await screen.findByRole("link", { name: "Users" })).toBeInTheDocument();
  });

  it("signs out", async () => {
    const api = installFetch({
      ...dashboardRoutes(),
      "GET /api/auth/state": () => json(signedIn()),
      "POST /api/auth/logout": () => new Response(null, { status: 204 }),
    });
    render(<App />);

    await userEvent.setup().click(await screen.findByRole("button", { name: "Sign out" }));

    expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();
    expect(api.callsTo("POST /api/auth/logout")).toHaveLength(1);
  });

  it("lets an administrator create users and keeps the form on failure", async () => {
    window.location.hash = "#/users";
    let creates = 0;
    const api = installFetch({
      ...dashboardRoutes(),
      "GET /api/auth/state": () => json(signedIn()),
      "GET /api/users": () => json({ users: [authUser("admin"), ...(creates > 1 ? [authUser("carol", "user")] : [])] }),
      "POST /api/users": () =>
        ++creates === 1 ? problem(409, "username_taken", "The username 'carol' is already taken.") : json(authUser("carol", "user"), 201),
    });
    render(<App />);
    const user = userEvent.setup();

    const table = await screen.findByRole("table");
    expect(within(table).getByText("You")).toBeInTheDocument();
    expect(within(table).getByText("Use the Account page")).toBeInTheDocument();

    await user.type(screen.getByLabelText("New username"), "carol");
    await user.type(screen.getByLabelText("Initial password"), "carol-password-1");
    await user.click(screen.getByRole("button", { name: "Create user" }));
    expect(await screen.findByText("The username 'carol' is already taken.")).toBeInTheDocument();
    expect(screen.getByLabelText("New username")).toHaveValue("carol");

    await user.click(screen.getByRole("button", { name: "Create user" }));
    expect(await screen.findByText("User carol created.")).toBeInTheDocument();
    await waitFor(() => expect(within(screen.getByRole("table")).getByText("carol")).toBeInTheDocument());
    expect(JSON.parse(api.callsTo("POST /api/users")[1]!.init!.body as string)).toEqual({ username: "carol", password: "carol-password-1", role: "user" });
  });

  it("changes the password from the Account page", async () => {
    window.location.hash = "#/account";
    const api = installFetch({
      ...dashboardRoutes(),
      "GET /api/auth/state": () => json(signedIn()),
      "POST /api/auth/password": () => new Response(null, { status: 204 }),
    });
    render(<App />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText("Current password"), "old-password-1");
    await user.type(screen.getByLabelText("New password"), "new-password-12");
    await user.type(screen.getByLabelText("Confirm new password"), "new-password-12");
    await user.click(screen.getByRole("button", { name: "Change password" }));

    expect(await screen.findByText("Password changed. Other sessions have been signed out.")).toBeInTheDocument();
    expect(JSON.parse(api.callsTo("POST /api/auth/password")[0]!.init!.body as string)).toEqual({
      currentPassword: "old-password-1",
      newPassword: "new-password-12",
    });
  });
});
