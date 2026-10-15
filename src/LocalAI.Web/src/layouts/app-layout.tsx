import type { ReactNode } from "react";
import { ActivityIcon, ChatIcon, CubeIcon, LockIcon } from "../components/icons";
import { ConnectionSummary } from "../features/system/connection-summary";
import type { PlatformStatus } from "../features/system/platform-status";
import type { Route } from "../hooks/use-hash-route";

const NAV: { route: Route; label: string; icon: ReactNode }[] = [
  { route: "chat", label: "Chat", icon: <ChatIcon /> },
  { route: "models", label: "Models", icon: <CubeIcon /> },
  { route: "system", label: "System", icon: <ActivityIcon /> },
];

interface AppLayoutProps {
  route: Route;
  status: PlatformStatus;
  generating: boolean;
  children: ReactNode;
}

export function AppLayout({ route, status, generating, children }: AppLayoutProps) {
  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="brand__mark" aria-hidden="true" />
          <div>
            <div className="brand__name">Local AI Infrastructure</div>
            <div className="brand__sub">Phase 1 · Ollama</div>
          </div>
        </div>

        <nav className="nav" aria-label="Main">
          {NAV.map((item) => (
            <a
              key={item.route}
              href={`#/${item.route}`}
              className={`nav__link${route === item.route ? " is-active" : ""}`}
              aria-current={route === item.route ? "page" : undefined}
            >
              {item.icon}
              <span>{item.label}</span>
            </a>
          ))}
        </nav>

        <ConnectionSummary status={status} generating={generating} />

        <p className="local-badge">
          <LockIcon />
          <span>Inference runs locally. No cloud AI service or API key is used.</span>
        </p>
      </aside>

      <main className="main">{children}</main>
    </div>
  );
}
