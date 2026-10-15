import { useCallback, useEffect, useState } from "react";

export type Route = "chat" | "models" | "system" | "users" | "account";

export interface Location {
  route: Route;
  /** Route parameter, e.g. the conversation id in #/chat/<id>. */
  param: string | null;
  /** Conversation open in the chat page, remembered while other pages are shown. */
  chatId: string | null;
}

const ROUTES: readonly Route[] = ["chat", "models", "system", "users", "account"];

export function parseHash(hash: string, previous?: Location): Location {
  const [head = "", ...rest] = hash.replace(/^#\/?/, "").split("/");
  const route = (ROUTES as readonly string[]).includes(head) ? (head as Route) : "chat";
  const param = route === "chat" ? rest.join("/") || null : null;
  return { route, param, chatId: route === "chat" ? param : (previous?.chatId ?? null) };
}

/**
 * Minimal hash router (three pages do not justify a routing dependency).
 * `navigate` updates state synchronously so it batches with other state updates.
 */
export function useHashRoute(): [Location, (path: string) => void] {
  const [location, setLocation] = useState<Location>(() => parseHash(window.location.hash));

  useEffect(() => {
    const onChange = () => setLocation((previous) => parseHash(window.location.hash, previous));
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);

  const navigate = useCallback((path: string) => {
    const hash = `#/${path.replace(/^\/+/, "")}`;
    setLocation((previous) => parseHash(hash, previous));
    if (window.location.hash !== hash) {
      window.location.hash = hash;
    }
  }, []);

  return [location, navigate];
}
