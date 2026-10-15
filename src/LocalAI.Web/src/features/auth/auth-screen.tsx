import { useState, type FormEvent } from "react";
import { ApiError } from "../../api/http";
import { LockIcon } from "../../components/icons";

interface AuthScreenProps {
  mode: "setup" | "login";
  notice?: string | null;
  onSubmit: (username: string, password: string) => Promise<void>;
}

const MIN_PASSWORD = 10;

/** First-run administrator setup and sign-in share one form. */
export function AuthScreen({ mode, notice, onSubmit }: AuthScreenProps) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const isSetup = mode === "setup";

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (isSetup && password !== confirm) {
      setError("The passwords do not match.");
      return;
    }
    setPending(true);
    setError(null);
    try {
      await onSubmit(username.trim(), password);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "Something went wrong. Try again.");
      setPending(false);
    }
  };

  return (
    <main className="auth">
      <form className="auth__card" onSubmit={submit} aria-labelledby="auth-title">
        <div className="brand">
          <span className="brand__mark" aria-hidden="true" />
          <div>
            <div className="brand__name">Local AI Infrastructure</div>
            <div className="brand__sub">{isSetup ? "First-run setup" : "Sign in"}</div>
          </div>
        </div>

        <h1 id="auth-title" className="auth__title">
          {isSetup ? "Create the administrator account" : "Sign in"}
        </h1>
        {isSetup && (
          <p className="muted small">
            This account manages users. Any conversations created before accounts existed will belong to it. There is
            no public sign-up: the administrator creates other accounts.
          </p>
        )}
        {notice && (
          <p className="auth__notice" role="status">
            {notice}
          </p>
        )}

        <label className="field">
          <span className="field__label">Username</span>
          <input
            className="input"
            name="username"
            autoComplete="username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            required
            minLength={3}
            maxLength={32}
            autoFocus
          />
        </label>
        <div className="field">
          <label className="field__label" htmlFor="auth-password">
            Password
          </label>
          <input
            id="auth-password"
            aria-describedby={isSetup ? "auth-password-hint" : undefined}
            className="input"
            type="password"
            name="password"
            autoComplete={isSetup ? "new-password" : "current-password"}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            minLength={isSetup ? MIN_PASSWORD : 1}
            maxLength={128}
          />
          {isSetup && (
            <span id="auth-password-hint" className="field__hint">
              At least {MIN_PASSWORD} characters.
            </span>
          )}
        </div>
        {isSetup && (
          <label className="field">
            <span className="field__label">Confirm password</span>
            <input
              className="input"
              type="password"
              name="confirm"
              autoComplete="new-password"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
              required
            />
          </label>
        )}

        {error && (
          <p className="auth__error" role="alert">
            {error}
          </p>
        )}

        <button type="submit" className="btn btn--primary auth__submit" disabled={pending}>
          <LockIcon />
          <span>{pending ? "Please wait…" : isSetup ? "Create account" : "Sign in"}</span>
        </button>
      </form>
    </main>
  );
}
