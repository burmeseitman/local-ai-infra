import { useCallback, useState, type FormEvent } from "react";
import { createUser, deleteUser, listUsers, updateUser } from "../api/auth";
import { ApiError } from "../api/http";
import { PlusIcon, RefreshIcon } from "../components/icons";
import { Notice } from "../components/notice";
import { useAsyncResource } from "../hooks/use-async-resource";
import type { AuthUser, UserRole } from "../types/api";
import { formatRelative } from "../utils/format";

const message = (error: unknown) => (error instanceof ApiError ? error.message : "The request failed.");

/** Administrator-only account management. */
export function UsersPage({ currentUser }: { currentUser: AuthUser }) {
  const users = useAsyncResource(useCallback((_force: boolean, signal: AbortSignal) => listUsers(signal), []));
  const [feedback, setFeedback] = useState<{ ok: boolean; text: string } | null>(null);

  const run = async (action: () => Promise<void>, success: string): Promise<boolean> => {
    try {
      await action();
      setFeedback({ ok: true, text: success });
      await users.reload();
      return true;
    } catch (error) {
      setFeedback({ ok: false, text: message(error) });
      return false;
    }
  };

  return (
    <section className="page" aria-labelledby="users-title">
      <header className="page-header">
        <div>
          <h1 id="users-title" className="page-title">
            Users
          </h1>
          <p className="page-subtitle">
            Each user sees only their own conversations. Changing a role or resetting a password signs that user out.
          </p>
        </div>
        <button type="button" className="btn" onClick={() => void users.reload()} disabled={users.refreshing}>
          <RefreshIcon className={users.refreshing ? "spin" : undefined} />
          <span>Refresh</span>
        </button>
      </header>

      {feedback && (
        <Notice tone={feedback.ok ? "info" : "error"} title={feedback.ok ? "Done" : "Action failed"}>
          {feedback.text}
        </Notice>
      )}
      {users.error && <Notice tone="error" title="Could not load users">{users.error.message}</Notice>}

      <CreateUserForm onCreate={(u, p, r) => run(async () => void (await createUser(u, p, r)), `User ${u} created.`)} />

      {users.data && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Username</th>
                <th scope="col">Role</th>
                <th scope="col">Created</th>
                <th scope="col">Last sign-in</th>
                <th scope="col">
                  <span className="visually-hidden">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {users.data.map((u) => (
                <UserRow
                  key={u.id}
                  user={u}
                  isSelf={u.id === currentUser.id}
                  onRole={(role) => run(() => updateUser(u.id, { role }), `${u.username} is now ${role === "admin" ? "an administrator" : "a user"}.`)}
                  onReset={(password) => run(() => updateUser(u.id, { password }), `Password for ${u.username} was reset.`)}
                  onDelete={() => run(() => deleteUser(u.id), `${u.username} and their conversations were deleted.`)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function CreateUserForm({ onCreate }: { onCreate: (username: string, password: string, role: UserRole) => Promise<boolean> }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState<UserRole>("user");
  const [pending, setPending] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setPending(true);
    const created = await onCreate(username.trim(), password, role);
    setPending(false);
    if (created) {
      setUsername("");
      setPassword("");
      setRole("user");
    }
  };

  return (
    <form className="form-card form-card--inline" onSubmit={submit} aria-label="Create user">
      <label className="field">
        <span className="field__label">New username</span>
        <input className="input" value={username} onChange={(e) => setUsername(e.target.value)} required minLength={3} maxLength={32} autoComplete="off" />
      </label>
      <label className="field">
        <span className="field__label">Initial password</span>
        <input className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} required minLength={10} maxLength={128} autoComplete="new-password" />
      </label>
      <label className="field">
        <span className="field__label">Role</span>
        <select className="select" value={role} onChange={(e) => setRole(e.target.value as UserRole)}>
          <option value="user">User</option>
          <option value="admin">Administrator</option>
        </select>
      </label>
      <button type="submit" className="btn btn--primary" disabled={pending}>
        <PlusIcon />
        <span>Create user</span>
      </button>
    </form>
  );
}

interface UserRowProps {
  user: AuthUser;
  isSelf: boolean;
  onRole: (role: UserRole) => Promise<boolean>;
  onReset: (password: string) => Promise<boolean>;
  onDelete: () => Promise<boolean>;
}

function UserRow({ user, isSelf, onRole, onReset, onDelete }: UserRowProps) {
  const [mode, setMode] = useState<"view" | "reset" | "delete">("view");
  const [password, setPassword] = useState("");

  return (
    <tr>
      <td>
        <span className="model-name">{user.username}</span>
        {isSelf && <span className="badge badge--accent">You</span>}
      </td>
      <td>{user.role === "admin" ? "Administrator" : "User"}</td>
      <td>{formatRelative(user.createdAt)}</td>
      <td>{formatRelative(user.lastLoginAt)}</td>
      <td className="actions">
        {mode === "reset" ? (
          <form
            className="inline-form"
            onSubmit={(e) => {
              e.preventDefault();
              void onReset(password).then((ok) => ok && setMode("view"));
            }}
          >
            <label className="visually-hidden" htmlFor={`reset-${user.id}`}>
              New password for {user.username}
            </label>
            <input id={`reset-${user.id}`} className="input" type="password" minLength={10} maxLength={128} required value={password} onChange={(e) => setPassword(e.target.value)} autoFocus />
            <button type="submit" className="btn btn--sm">Save</button>
            <button type="button" className="btn btn--sm btn--ghost" onClick={() => setMode("view")}>Cancel</button>
          </form>
        ) : mode === "delete" ? (
          <span className="inline-form">
            <span className="small">Delete {user.username} and all their conversations?</span>
            <button type="button" className="btn btn--sm btn--danger" onClick={() => void onDelete()}>Delete</button>
            <button type="button" className="btn btn--sm btn--ghost" onClick={() => setMode("view")}>Cancel</button>
          </span>
        ) : isSelf ? (
          <span className="muted small">Use the Account page</span>
        ) : (
          <span className="inline-form">
            <button type="button" className="btn btn--sm" onClick={() => void onRole(user.role === "admin" ? "user" : "admin")}>
              {user.role === "admin" ? "Make user" : "Make admin"}
            </button>
            <button type="button" className="btn btn--sm" onClick={() => { setPassword(""); setMode("reset"); }}>
              Reset password
            </button>
            <button type="button" className="btn btn--sm btn--danger" aria-label={`Delete ${user.username}`} onClick={() => setMode("delete")}>
              Delete
            </button>
          </span>
        )}
      </td>
    </tr>
  );
}
