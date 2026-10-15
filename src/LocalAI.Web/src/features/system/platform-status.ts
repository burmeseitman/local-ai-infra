import type { ApiError } from "../../api/http";
import type { Tone } from "../../components/status-dot";
import type { SystemOverview } from "../../types/api";

export interface LayerStatus {
  tone: Tone;
  label: string;
}

export interface PlatformStatus {
  api: LayerStatus;
  provider: LayerStatus;
  models: LayerStatus;
  inference: LayerStatus;
  /** True when a prompt can reasonably be sent right now. */
  canChat: boolean;
}

/**
 * Derives the four independent health layers. A running Ollama does not imply a
 * usable model, and an installed model does not prove inference works.
 */
export function derivePlatformStatus(
  overview: SystemOverview | null,
  error: ApiError | null,
  loading: boolean,
): PlatformStatus {
  const unknown: LayerStatus = { tone: "idle", label: "Unknown" };
  if (!overview) {
    const api: LayerStatus = loading ? { tone: "idle", label: "Checking…" } : { tone: "error", label: "Unreachable" };
    return { api, provider: unknown, models: unknown, inference: unknown, canChat: false };
  }

  const stale = error !== null; // last poll failed; show what we last knew, flagged.
  const api: LayerStatus = stale ? { tone: "error", label: "Unreachable" } : { tone: "ok", label: "Healthy" };
  const p = overview.provider;
  const provider: LayerStatus = p.reachable
    ? { tone: "ok", label: `Connected${p.version ? ` · v${p.version}` : ""}` }
    : { tone: "error", label: "Unreachable" };

  let models: LayerStatus;
  if (!p.reachable || p.usableModelCount == null) {
    models = unknown;
  } else if (p.usableModelCount === 0) {
    models = { tone: "warn", label: "No model installed" };
  } else {
    models = { tone: "ok", label: `${p.usableModelCount} available` };
  }

  const inf = overview.inference;
  let inference: LayerStatus;
  if (inf.activeGenerations > 0) {
    inference = { tone: "busy", label: `${inf.activeGenerations} generating` };
  } else if (inf.lastSuccessAt && (!inf.lastFailureAt || inf.lastSuccessAt >= inf.lastFailureAt)) {
    inference = { tone: "ok", label: "Verified" };
  } else if (inf.lastFailureAt) {
    inference = { tone: "warn", label: "Last request failed" };
  } else {
    inference = { tone: "idle", label: "Not yet verified" };
  }

  return {
    api,
    provider,
    models,
    inference,
    canChat: !stale && p.reachable && (p.usableModelCount ?? 0) > 0,
  };
}
