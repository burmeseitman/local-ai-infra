import { useState, type FormEvent } from "react";
import { changePassword } from "../api/auth";
import { ApiError } from "../api/http";
import { Notice } from "../components/notice";
import type { AuthUser } from "../types/api";
import { formatTimestamp } from "../utils/format";

export function AccountPage({ user }: { user: AuthUser }) {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [pending, setPending] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (next !== confirm) {
      setResult({ ok: false, message: "The new passwords do not match." });
      return;
    }
    setPending(true);
    try {
      await changePassword(current, next);
      setResult({ ok: true, message: "Password changed. Other sessions have been signed out." });
      setCurrent("");
      setNext("");
      setConfirm("");
    } catch (error) {
      setResult({ ok: false, message: error instanceof ApiError ? error.message : "Could not change the password." });
    } finally {
      setPending(false);
    }
  };

  return (
    <section className="page" aria-labelledby="account-title">
      <header className="page-header">
        <div>
          <h1 id="account-title" className="page-title">
            Account
          </h1>
          <p className="page-subtitle">
            Signed in as <strong>{user.username}</strong> ({user.role}). Last sign-in {formatTimestamp(user.lastLoginAt)}.
          </p>
        </div>
      </header>

      <form className="form-card" onSubmit={submit} aria-labelledby="password-title">
        <h2 id="password-title" className="section-title">
          Change password
        </h2>
        {result && (
          <Notice tone={result.ok ? "info" : "error"} title={result.ok ? "Done" : "Not changed"}>
            {result.message}
          </Notice>
        )}
        <label className="field">
          <span className="field__label">Current password</span>
          <input className="input" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
        </label>
        <div className="field">
          <label className="field__label" htmlFor="new-password">
            New password
          </label>
          <input id="new-password" aria-describedby="new-password-hint" className="input" type="password" autoComplete="new-password" minLength={10} maxLength={128} value={next} onChange={(e) => setNext(e.target.value)} required />
          <span id="new-password-hint" className="field__hint">
            At least 10 characters.
          </span>
        </div>
        <label className="field">
          <span className="field__label">Confirm new password</span>
          <input className="input" type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
        </label>
        <button type="submit" className="btn btn--primary" disabled={pending}>
          {pending ? "Saving…" : "Change password"}
        </button>
      </form>
    </section>
  );
}
