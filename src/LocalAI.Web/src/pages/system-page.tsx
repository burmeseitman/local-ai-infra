import type { ReactNode } from "react";
import { RefreshIcon } from "../components/icons";
import { Notice } from "../components/notice";
import { StatusDot } from "../components/status-dot";
import type { LayerStatus, PlatformStatus } from "../features/system/platform-status";
import type { AsyncResource } from "../hooks/use-async-resource";
import type { SystemOverview } from "../types/api";
import { formatDuration, formatRelative, formatTimestamp, formatUptime, summarizeStats } from "../utils/format";

interface SystemPageProps {
  overview: AsyncResource<SystemOverview>;
  status: PlatformStatus;
  generatingHere: boolean;
}

function Layer({ title, layer, children }: { title: string; layer: LayerStatus; children: ReactNode }) {
  return (
    <article className="layer">
      <header className="layer__head">
        <h2 className="layer__title">{title}</h2>
        <StatusDot tone={layer.tone} label={layer.label} />
      </header>
      <dl className="kv">{children}</dl>
    </article>
  );
}

function Row({ k, v }: { k: string; v: ReactNode }) {
  return (
    <>
      <dt>{k}</dt>
      <dd>{v}</dd>
    </>
  );
}

function Stat({ label, value, hint }: { label: string; value: ReactNode; hint?: string }) {
  return (
    <div className="stat">
      <div className="stat__value">{value}</div>
      <div className="stat__label">{label}</div>
      {hint && <div className="stat__hint">{hint}</div>}
    </div>
  );
}

export function SystemPage({ overview, status, generatingHere }: SystemPageProps) {
  const data = overview.data;

  return (
    <section className="page" aria-labelledby="system-title">
      <header className="page-header">
        <div>
          <h1 id="system-title" className="page-title">
            System
          </h1>
          <p className="page-subtitle">
            Live values from <code>/api/system/overview</code>, polled every 10 s. Last update{" "}
            {overview.lastUpdated ? formatRelative(new Date(overview.lastUpdated).toISOString()) : "pending"}.
          </p>
        </div>
        <button type="button" className="btn" onClick={() => void overview.reload()} disabled={overview.refreshing}>
          <RefreshIcon className={overview.refreshing ? "spin" : undefined} />
          <span>Refresh</span>
        </button>
      </header>

      {overview.error && (
        <Notice tone="error" title="API unreachable">
          {overview.error.message} {data && "Values below are from the last successful check."}
        </Notice>
      )}
      {!data && overview.initialLoading && <p className="muted">Loading system status…</p>}

      {data && (
        <>
          <div className="layers">
            <Layer title="Backend API" layer={status.api}>
              <Row k="Version" v={data.api.version} />
              <Row k="Environment" v={data.api.environment} />
              <Row k="Uptime" v={formatUptime(data.api.uptimeSeconds)} />
              <Row k="Started" v={formatTimestamp(data.api.startedAt)} />
            </Layer>

            <Layer title="Ollama service" layer={status.provider}>
              <Row k="Version" v={data.provider.version ?? "—"} />
              <Row k="Probe latency" v={formatDuration(data.provider.latencyMs)} />
              <Row k="Last successful check" v={formatRelative(data.provider.lastSuccessfulCheckAt)} />
              {data.provider.error && <Row k="Error" v={data.provider.error} />}
            </Layer>

            <Layer title="Model availability" layer={status.models}>
              <Row k="Installed" v={data.provider.installedModelCount ?? "unavailable"} />
              <Row k="Usable (local)" v={data.provider.usableModelCount ?? "unavailable"} />
              <Row k="Default model" v={data.provider.defaultModel ? <code>{data.provider.defaultModel}</code> : "none"} />
              <Row k="Default installed" v={data.provider.reachable ? (data.provider.defaultModelInstalled ? "yes" : "no") : "unknown"} />
            </Layer>

            <Layer title="Inference" layer={status.inference}>
              <Row k="Last success" v={formatRelative(data.inference.lastSuccessAt)} />
              <Row k="Last model" v={data.inference.lastCompletionModel ?? "—"} />
              <Row k="Last result" v={summarizeStats(data.inference.lastCompletion) ?? "—"} />
              <Row k="This browser" v={generatingHere ? "generating" : "idle"} />
            </Layer>
          </div>
          <p className="muted small">
            These layers are independent: Ollama can be up with no model installed, and an installed model is only
            proven to work once a real generation succeeds. Health checks never run inference.
          </p>

          <h2 className="section-title">Requests since API start</h2>
          <div className="stats">
            <Stat label="Active" value={data.inference.activeGenerations} hint={`limit ${data.limits.maxConcurrentGenerations}`} />
            <Stat label="Total" value={data.inference.totalRequests} />
            <Stat label="Succeeded" value={data.inference.succeeded} />
            <Stat label="Failed" value={data.inference.failed} />
            <Stat label="Cancelled" value={data.inference.cancelled} />
          </div>
          <p className="muted small">
            Counted in API memory since {formatTimestamp(data.inference.since)}; reset when the API restarts.
          </p>

          <h2 className="section-title">Recent failures</h2>
          {data.inference.recentFailures.length === 0 ? (
            <p className="muted">No failures recorded.</p>
          ) : (
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th scope="col">When</th>
                    <th scope="col">Code</th>
                    <th scope="col">Model</th>
                  </tr>
                </thead>
                <tbody>
                  {data.inference.recentFailures.map((f) => (
                    <tr key={`${f.at}-${f.code}`}>
                      <td title={formatTimestamp(f.at)}>{formatRelative(f.at)}</td>
                      <td className="mono">{f.code}</td>
                      <td className="mono">{f.model ?? "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <h2 className="section-title">Limits</h2>
          <dl className="kv kv--grid">
            <Row k="Max message length" v={`${data.limits.maxMessageLength.toLocaleString()} chars`} />
            <Row k="Max conversation" v={`${data.limits.maxConversationLength.toLocaleString()} chars`} />
            <Row k="Max messages" v={data.limits.maxMessages} />
            <Row k="Default / max tokens" v={`${data.limits.defaultMaxTokens} / ${data.limits.maxTokensLimit}`} />
            <Row k="Inference timeout" v={`${data.limits.inferenceTimeoutSeconds} s`} />
            <Row k="Remote models" v={data.limits.allowRemoteModels ? "allowed" : "blocked"} />
          </dl>
        </>
      )}
    </section>
  );
}
