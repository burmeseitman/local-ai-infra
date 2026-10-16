import type { ReactNode } from "react";
import { ActivityIcon, ChatIcon, CloudIcon, CubeIcon, FileIcon, LockIcon, UserIcon, UsersIcon } from "../components/icons";
import { ConnectionSummary } from "../features/system/connection-summary";
import type { PlatformStatus } from "../features/system/platform-status";
import type { Route } from "../hooks/use-hash-route";
import type { AuthUser } from "../types/api";

const NAV: { route: Route; label: string; icon: ReactNode; adminOnly?: boolean }[] = [
  { route: "chat", label: "Chat", icon: <ChatIcon /> },
  { route: "documents", label: "Documents", icon: <FileIcon /> },
  { route: "models", label: "Models", icon: <CubeIcon /> },
  { route: "system", label: "System", icon: <ActivityIcon /> },
  { route: "users", label: "Users", icon: <UsersIcon />, adminOnly: true },
];

interface AppLayoutProps {
  route: Route;
  user: AuthUser;
  onSignOut: () => Promise<void>;
  /** Link for the Chat item; keeps the open conversation when returning from other pages. */
  chatHref: string;
  status: PlatformStatus;
  generating: boolean;
  /** The selected chat model runs on Ollama's cloud rather than this machine. */
  cloudModel: boolean;
  children: ReactNode;
}

export function AppLayout({ route, user, onSignOut, chatHref, status, generating, cloudModel, children }: AppLayoutProps) {
  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="brand__mark" aria-hidden="true" />
          <div>
            <div className="brand__name">Local AI Infrastructure</div>
            <div className="brand__sub">Phase 3 · Ollama</div>
          </div>
        </div>

        <nav className="nav" aria-label="Main">
          {NAV.filter((item) => !item.adminOnly || user.role === "admin").map((item) => (
            <a
              key={item.route}
              href={item.route === "chat" ? chatHref : `#/${item.route}`}
              className={`nav__link${route === item.route ? " is-active" : ""}`}
              aria-current={route === item.route ? "page" : undefined}
            >
              {item.icon}
              <span>{item.label}</span>
            </a>
          ))}
        </nav>

        <ConnectionSummary status={status} generating={generating} />

        <section className="account" aria-label="Signed-in user">
          <a href="#/account" className={`account__user${route === "account" ? " is-active" : ""}`} title="Account settings">
            <UserIcon />
            <span className="account__name">{user.username}</span>
            {user.role === "admin" && <span className="badge">admin</span>}
          </a>
          <button type="button" className="btn btn--ghost btn--sm" onClick={() => void onSignOut()}>
            Sign out
          </button>
        </section>

        {cloudModel ? (
          <p className="local-badge local-badge--cloud" role="status">
            <CloudIcon />
            <span>A cloud model is selected: prompts are sent to ollama.com. Documents are still indexed locally.</span>
          </p>
        ) : (
          <p className="local-badge">
            <LockIcon />
            <span>Inference runs locally. No cloud AI service or API key is used.</span>
          </p>
        )}
      </aside>

      <main className="main">{children}</main>
    </div>
  );
}
