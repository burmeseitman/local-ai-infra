import { useCallback, useEffect, useState } from "react";
import { getAuthState, login, logout, setup } from "../../api/auth";
import { ApiError, onUnauthorized } from "../../api/http";
import type { AuthUser } from "../../types/api";

export type AuthStatus =
  | { kind: "loading" }
  | { kind: "unreachable"; message: string }
  | { kind: "setup" }
  | { kind: "signedOut"; notice: string | null }
  | { kind: "signedIn"; user: AuthUser };

export interface AuthController {
  status: AuthStatus;
  signIn: (username: string, password: string) => Promise<void>;
  completeSetup: (username: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
  retry: () => void;
}

/** Session state for the whole app. Any "unauthenticated" API response signs the UI out. */
export function useAuth(): AuthController {
  const [status, setStatus] = useState<AuthStatus>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    getAuthState(controller.signal).then(
      (state) =>
        setStatus(
          state.user
            ? { kind: "signedIn", user: state.user }
            : state.setupRequired
              ? { kind: "setup" }
              : { kind: "signedOut", notice: null },
        ),
      (error: unknown) => {
        if (!controller.signal.aborted) {
          setStatus({
            kind: "unreachable",
            message: error instanceof ApiError ? error.message : "Cannot reach the API.",
          });
        }
      },
    );
    return () => controller.abort();
  }, [attempt]);

  useEffect(() => {
    onUnauthorized(() =>
      setStatus((current) =>
        current.kind === "signedIn" ? { kind: "signedOut", notice: "Your session has ended. Please sign in again." } : current,
      ),
    );
    return () => onUnauthorized(null);
  }, []);

  const signIn = useCallback(async (username: string, password: string) => {
    const user = await login(username, password);
    setStatus({ kind: "signedIn", user });
  }, []);

  const completeSetup = useCallback(async (username: string, password: string) => {
    const user = await setup(username, password);
    setStatus({ kind: "signedIn", user });
  }, []);

  const signOut = useCallback(async () => {
    try {
      await logout();
    } finally {
      setStatus({ kind: "signedOut", notice: null });
    }
  }, []);

  const retry = useCallback(() => {
    setStatus({ kind: "loading" });
    setAttempt((n) => n + 1);
  }, []);

  return { status, signIn, completeSetup, signOut, retry };
}
