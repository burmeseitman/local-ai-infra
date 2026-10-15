import type { CompletionStats } from "../types/api";

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes)) {
    return "—";
  }
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(value >= 10 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}

export function formatDuration(ms: number | null | undefined): string {
  if (ms == null || !Number.isFinite(ms)) {
    return "—";
  }
  if (ms < 1000) {
    return `${Math.round(ms)} ms`;
  }
  const seconds = ms / 1000;
  if (seconds < 60) {
    return `${seconds.toFixed(1)} s`;
  }
  const minutes = Math.floor(seconds / 60);
  return `${minutes}m ${Math.round(seconds % 60)}s`;
}

export function formatUptime(totalSeconds: number): string {
  const days = Math.floor(totalSeconds / 86_400);
  const hours = Math.floor((totalSeconds % 86_400) / 3_600);
  const minutes = Math.floor((totalSeconds % 3_600) / 60);
  if (days > 0) {
    return `${days}d ${hours}h`;
  }
  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }
  return `${minutes}m ${Math.floor(totalSeconds % 60)}s`;
}

export function formatRelative(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) {
    return "never";
  }
  const time = Date.parse(iso);
  if (Number.isNaN(time)) {
    return "—";
  }
  const seconds = Math.round((now - time) / 1000);
  if (seconds < 5) {
    return "just now";
  }
  if (seconds < 60) {
    return `${seconds}s ago`;
  }
  if (seconds < 3_600) {
    return `${Math.floor(seconds / 60)}m ago`;
  }
  if (seconds < 86_400) {
    return `${Math.floor(seconds / 3_600)}h ago`;
  }
  return new Date(time).toLocaleDateString();
}

export function formatTimestamp(iso: string | null | undefined): string {
  if (!iso) {
    return "—";
  }
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
}

/** Short, human-readable summary of provider-reported stats; omits unknown values. */
export function summarizeStats(stats: CompletionStats | null | undefined): string | null {
  if (!stats) {
    return null;
  }
  const parts: string[] = [];
  if (stats.completionTokens != null) {
    parts.push(`${stats.completionTokens} tokens`);
  }
  if (stats.tokensPerSecond != null) {
    parts.push(`${stats.tokensPerSecond.toFixed(1)} tok/s`);
  }
  if (stats.totalDurationMs != null) {
    parts.push(formatDuration(stats.totalDurationMs));
  }
  if (stats.loadDurationMs != null && stats.loadDurationMs >= 500) {
    parts.push(`load ${formatDuration(stats.loadDurationMs)}`);
  }
  return parts.length > 0 ? parts.join(" · ") : null;
}
