import { RefreshIcon } from "../components/icons";
import { Notice } from "../components/notice";
import { StatusDot } from "../components/status-dot";
import { NoModelsHelp } from "../features/models/no-models-help";
import type { AsyncResource } from "../hooks/use-async-resource";
import type { ModelListResponse } from "../types/api";
import { formatBytes, formatRelative } from "../utils/format";

interface ModelsPageProps {
  models: AsyncResource<ModelListResponse>;
  selectedModel: string | null;
  onSelectModel: (name: string) => void;
  selectionLocked: boolean;
}

export function ModelsPage({ models, selectedModel, onSelectModel, selectionLocked }: ModelsPageProps) {
  const list = models.data?.models ?? [];

  return (
    <section className="page" aria-labelledby="models-title">
      <header className="page-header">
        <div>
          <h1 id="models-title" className="page-title">
            Models
          </h1>
          <p className="page-subtitle">
            Models installed in {models.data?.provider ?? "the provider"}. Updated{" "}
            {formatRelative(models.data?.fetchedAt)}.
          </p>
        </div>
        <button type="button" className="btn" onClick={() => void models.reload(true)} disabled={models.refreshing}>
          <RefreshIcon className={models.refreshing ? "spin" : undefined} />
          <span>Refresh</span>
        </button>
      </header>

      {models.error && (
        <Notice tone="error" title="Could not load models">
          {models.error.message}
          {models.data && " Showing the last known list."}
        </Notice>
      )}

      {models.initialLoading && !models.data && <p className="muted">Loading models…</p>}

      {models.data && list.length === 0 && (
        <Notice tone="warn" title="No models installed">
          <NoModelsHelp suggested={models.data.defaultModel} />
        </Notice>
      )}

      {list.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Model</th>
                <th scope="col">Family</th>
                <th scope="col">Parameters</th>
                <th scope="col">Quantization</th>
                <th scope="col" className="num">
                  Size
                </th>
                <th scope="col">Status</th>
                <th scope="col">
                  <span className="visually-hidden">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {list.map((m) => {
                const selected = m.name === selectedModel;
                return (
                  <tr key={m.name} className={selected ? "is-selected" : undefined}>
                    <td>
                      <div className="model-name">{m.displayName}</div>
                      <div className="mono muted small">
                        {m.tag ? `tag ${m.tag}` : "no tag"}
                        {m.digest ? ` · ${m.digest}` : ""}
                      </div>
                    </td>
                    <td>{m.family ?? "—"}</td>
                    <td>{m.parameterSize ?? "—"}</td>
                    <td className="mono">{m.quantizationLevel ?? "—"}</td>
                    <td className="num mono">{m.isRemote ? "remote" : formatBytes(m.sizeBytes)}</td>
                    <td>
                      <div className="badges">
                        {m.isAvailable ? (
                          <StatusDot tone="ok" label="Available" />
                        ) : (
                          <StatusDot tone="warn" label="Remote · disabled" />
                        )}
                        {m.isDefault && <span className="badge">Default</span>}
                        {selected && <span className="badge badge--accent">Selected</span>}
                      </div>
                    </td>
                    <td className="actions">
                      <button
                        type="button"
                        className="btn btn--sm"
                        disabled={!m.isAvailable || selected || selectionLocked}
                        onClick={() => onSelectModel(m.name)}
                      >
                        {selected ? "In use" : "Use"}
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <p className="muted small">
        Remote models (for example Ollama &quot;cloud&quot; models) run on a third-party host and are disabled unless{" "}
        <code>Chat__AllowRemoteModels=true</code> is configured.
      </p>
    </section>
  );
}
