import type { DocumentInfo, DocumentListResponse } from "../types/api";
import { API_BASE_URL, CREDENTIALS, CSRF_HEADERS, getJson, networkError, sendJson, toApiError } from "./http";

export function listDocuments(signal?: AbortSignal): Promise<DocumentListResponse> {
  return getJson<DocumentListResponse>("/api/documents", signal);
}

/** Multipart upload; indexing continues in the background (status "processing"). */
export async function uploadDocument(file: File): Promise<DocumentInfo> {
  const form = new FormData();
  form.append("file", file);
  let response: Response;
  try {
    response = await fetch(`${API_BASE_URL}/api/documents`, {
      method: "POST",
      headers: { ...CSRF_HEADERS, Accept: "application/json" }, // browser sets the multipart boundary
      body: form,
      credentials: CREDENTIALS,
    });
  } catch {
    throw networkError();
  }
  if (!response.ok) {
    throw await toApiError(response);
  }
  return (await response.json()) as DocumentInfo;
}

export async function deleteDocument(id: string): Promise<void> {
  await sendJson("DELETE", `/api/documents/${encodeURIComponent(id)}`);
}
