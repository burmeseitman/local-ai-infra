import { AuthScreen } from "./features/auth/auth-screen";
import { useAuth } from "./features/auth/use-auth";
import { Dashboard } from "./dashboard";

/** Root: resolves the session first, then shows setup, sign-in or the dashboard. */
export function App() {
  const auth = useAuth();
  const { status } = auth;

  switch (status.kind) {
    case "loading":
      return (
        <main className="auth">
          <p className="muted">Loading…</p>
        </main>
      );
    case "unreachable":
      return (
        <main className="auth">
          <div className="auth__card" role="alert">
            <h1 className="auth__title">Cannot reach the API</h1>
            <p className="muted">{status.message} Check that the api service is running.</p>
            <button type="button" className="btn" onClick={auth.retry}>
              Try again
            </button>
          </div>
        </main>
      );
    case "setup":
      return <AuthScreen mode="setup" onSubmit={auth.completeSetup} />;
    case "signedOut":
      return <AuthScreen mode="login" notice={status.notice} onSubmit={auth.signIn} />;
    case "signedIn":
      // Keyed by user so nothing from a previous session survives a different sign-in.
      return <Dashboard key={status.user.id} user={status.user} onSignOut={auth.signOut} />;
  }
}
