import { StatusDot } from "../../components/status-dot";
import type { PlatformStatus } from "./platform-status";

/** Compact status block for the sidebar. */
export function ConnectionSummary({ status, generating }: { status: PlatformStatus; generating: boolean }) {
  const rows = [
    { name: "API", layer: status.api },
    { name: "Ollama", layer: status.provider },
    { name: "Models", layer: status.models },
  ];
  return (
    <section className="conn" aria-label="Connection status">
      <h2 className="conn__title">Status</h2>
      <ul className="conn__list">
        {rows.map((row) => (
          <li key={row.name} className="conn__row">
            <span className="conn__name">{row.name}</span>
            <StatusDot tone={row.layer.tone} label={row.layer.label} />
          </li>
        ))}
        <li className="conn__row">
          <span className="conn__name">Chat</span>
          <StatusDot tone={generating ? "busy" : "idle"} label={generating ? "Generating" : "Idle"} />
        </li>
      </ul>
    </section>
  );
}
