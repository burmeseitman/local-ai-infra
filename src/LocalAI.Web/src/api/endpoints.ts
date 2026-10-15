import type { ModelListResponse, SystemOverview } from "../types/api";
import { getJson } from "./http";

export function fetchModels(refresh: boolean, signal?: AbortSignal): Promise<ModelListResponse> {
  return getJson<ModelListResponse>(`/api/models${refresh ? "?refresh=true" : ""}`, signal);
}

export function fetchOverview(signal?: AbortSignal): Promise<SystemOverview> {
  return getJson<SystemOverview>("/api/system/overview", signal);
}

export { streamChat } from "./chat-stream";
