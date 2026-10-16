import { RefreshIcon } from "../../components/icons";
import type { ModelInfo } from "../../types/api";

interface ModelSelectorProps {
  models: readonly ModelInfo[];
  selected: string | null;
  loading: boolean;
  disabled?: boolean;
  onSelect: (name: string) => void;
  onRefresh: () => void;
}

export function ModelSelector({ models, selected, loading, disabled, onSelect, onRefresh }: ModelSelectorProps) {
  const usable = models.filter((m) => m.isAvailable);
  const local = usable.filter((m) => !m.isRemote);
  const cloud = usable.filter((m) => m.isRemote);
  // Embedding models are only for documents; they never appear as chat choices.
  const unusable = models.filter((m) => !m.isAvailable && !m.isEmbedding);
  const option = (m: ModelInfo) => (
    <option key={m.name} value={m.name}>
      {m.name}
      {m.parameterSize ? ` · ${m.parameterSize}` : ""}
      {m.isRemote ? " · cloud" : ""}
    </option>
  );

  return (
    <div className="model-selector">
      <label className="model-selector__label" htmlFor="model-select">
        Model
      </label>
      <select
        id="model-select"
        className="select"
        value={selected ?? ""}
        disabled={disabled || usable.length === 0}
        onChange={(e) => onSelect(e.target.value)}
      >
        {usable.length === 0 && <option value="">{loading ? "Loading models…" : "No models installed"}</option>}
        {cloud.length > 0 ? (
          <>
            <optgroup label="Local (this machine)">{local.map(option)}</optgroup>
            <optgroup label="Cloud (runs on ollama.com)">{cloud.map(option)}</optgroup>
          </>
        ) : (
          local.map(option)
        )}
        {unusable.map((m) => (
          <option key={m.name} value={m.name} disabled>
            {m.name} (cloud — disabled)
          </option>
        ))}
      </select>
      <button
        type="button"
        className="btn btn--ghost btn--icon"
        onClick={onRefresh}
        disabled={loading}
        aria-label="Refresh model list"
        title="Refresh model list"
      >
        <RefreshIcon className={loading ? "spin" : undefined} />
      </button>
    </div>
  );
}
