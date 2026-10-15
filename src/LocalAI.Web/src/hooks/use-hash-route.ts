import { useEffect, useState } from "react";

export type Route = "chat" | "models" | "system";

const ROUTES: readonly Route[] = ["chat", "models", "system"];

function parse(hash: string): Route {
  const candidate = hash.replace(/^#\/?/, "");
  return (ROUTES as readonly string[]).includes(candidate) ? (candidate as Route) : "chat";
}

/** Minimal hash router: three static pages do not justify a routing dependency. */
export function useHashRoute(): Route {
  const [route, setRoute] = useState<Route>(() => parse(window.location.hash));

  useEffect(() => {
    const onChange = () => setRoute(parse(window.location.hash));
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);

  return route;
}
